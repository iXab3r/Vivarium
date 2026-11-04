using System.Diagnostics;
using System.Text;
using Vivarium.Contracts;
using Vivarium.Contracts.V1;

namespace Vivarium.Agent;

public static partial class BuildExecutor
{
    private static string PrivateInputs(string workRoot, string buildId) => ResolveUnder(workRoot, ".inputs/" + buildId);

    private static async Task<VcsCheckoutReceipt> CheckoutAsync(string workRoot, string workdir, string buildId,
        VcsCheckout source, BuildStopController stop, Action<Process?>? observeProcess)
    {
        VcsCheckoutProfile.Validate(source);
        var gitDirectory = ResolveUnder(PrivateInputs(workRoot, buildId), "git");
        Directory.CreateDirectory(gitDirectory);
        var phase = "git_init_failed";
        try
        {
            await GitAsync(["init", "--bare", "--template=", gitDirectory]);
            // Native Windows Git bounds an absolute GIT_DIR before applying long-path support.
            // Every invocation already runs inside this exact private directory.
            string[] prefix = ["--git-dir=.", "--work-tree=" + workdir, "-c", "core.bare=false"];
            phase = "fetch_failed";
            await GitAsync([.. prefix, "fetch", "--no-tags", "--depth=1", "--no-recurse-submodules", "--", source.Url, source.Revision]);
            var fetched = await GitAsync([.. prefix, "rev-parse", "--verify", "FETCH_HEAD^{commit}"]);
            if (fetched != source.Revision) return new() { FailureCode = "revision_mismatch" };
            phase = "checkout_failed";
            await GitAsync([.. prefix, "checkout", "--force", "--detach", source.Revision]);
            var actual = await GitAsync([.. prefix, "rev-parse", "--verify", "HEAD^{commit}"]);
            if (actual != source.Revision) return new() { FailureCode = "revision_mismatch" };
            foreach (var excluded in source.ExcludedDirectories)
            {
                var path = ResolveUnder(workdir, excluded);
                var directory = new DirectoryInfo(path);
                if (directory.Exists) directory.Delete(recursive: directory.LinkTarget is null);
                else if (File.Exists(path) || new FileInfo(path).LinkTarget is not null) File.Delete(path);
            }
            return new() { Succeeded = true, Revision = actual };
        }
        catch (System.ComponentModel.Win32Exception) { return new() { FailureCode = "git_unavailable" }; }
        catch (IOException) { return new() { FailureCode = phase }; }
        catch (UnauthorizedAccessException) { return new() { FailureCode = "checkout_denied" }; }

        async Task<string> GitAsync(string[] arguments)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = gitDirectory, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            VcsCheckoutProfile.IsolateEnvironment(start.Environment);
            var home = ResolveUnder(PrivateInputs(workRoot, buildId), "git-home"); Directory.CreateDirectory(home);
            foreach (var key in new[] { "HOME", "USERPROFILE", "XDG_CONFIG_HOME", "CURL_HOME" }) start.Environment[key] = home;
            if (OperatingSystem.IsWindows()) { start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.longpaths=true"); }
            foreach (var argument in VcsCheckoutProfile.ConfigurationArguments.Concat(arguments)) start.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = start };
            if (!process.Start()) throw new IOException("The Git process did not start.");
            try { observeProcess?.Invoke(process); }
            catch { await ForceTerminateAsync(process); throw; }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.GracefulToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            using var readers = new CancellationTokenSource();
            var readerFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<string> ReadBoundedAsync(Stream stream)
            {
                try { return await ReadAsync(stream, readers.Token); }
                catch { readerFailed.TrySetResult(); throw; }
            }
            var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream);
            var stderr = ReadBoundedAsync(process.StandardError.BaseStream);
            try
            {
                var exit = process.WaitForExitAsync(deadline.Token);
                var first = await Task.WhenAny(exit, readerFailed.Task);
                // A failed bounded reader must terminate the producer instead of blocking on its pipe.
                if (first == readerFailed.Task) await ForceTerminateAsync(process);
                await exit;
            }
            catch (OperationCanceledException) when (stop.Mode != BuildStopMode.Unspecified)
            { await StopProcessAsync(process, stop, readerFailed.Task); throw; }
            catch (OperationCanceledException)
            { await ForceTerminateAsync(process); throw new IOException("Git checkout exceeded its deadline."); }
            catch { await ForceTerminateAsync(process); throw; }
            finally
            {
                if (process.HasExited) observeProcess?.Invoke(null);
                readers.CancelAfter(OutputDrainTimeout);
                try { await Task.WhenAll(stdout, stderr); }
                catch (Exception error) when (error is OperationCanceledException or IOException)
                { /* Preserve a pending stop/termination failure; bounded-reader failure is checked below. */ }
            }
            if (!stdout.IsCompletedSuccessfully || !stderr.IsCompletedSuccessfully || process.ExitCode != 0)
                throw new IOException("Git checkout did not complete.");
            return stdout.Result.TrimEnd('\r', '\n');
        }

        static async Task<string> ReadAsync(Stream input, CancellationToken token)
        {
            using var output = new MemoryStream(); var buffer = new byte[4096];
            int count;
            while ((count = await input.ReadAsync(buffer, token)) != 0)
            {
                if (output.Length + count > 65536) throw new IOException("Git output exceeded its bound.");
                output.Write(buffer, 0, count);
            }
            return Encoding.UTF8.GetString(output.ToArray());
        }
    }
}
