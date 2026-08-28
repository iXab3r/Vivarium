using System.Text;
using Vivarium.Agent;
using Vivarium.Contracts.V1;

namespace Vivarium.Tests;

[TestFixture]
public sealed class BuildExecutorTests
{
    private string root = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "vivarium-executor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Best effort: a failed process must not hide its assertion.
        }
    }

    [Test]
    public async Task Execute_sets_the_portable_environment_and_recreates_results_for_each_step()
    {
        var scriptPath = Path.Combine(root, OperatingSystem.IsWindows() ? "capture.cmd" : "capture.sh");
        await File.WriteAllTextAsync(scriptPath, OperatingSystem.IsWindows()
            ? "@echo off\r\nif exist \"%VIVARIUM_RESULTS_DIR%\\.\" (echo RESULTS_EXISTS=true) else (echo RESULTS_EXISTS=false)\r\nset VIVARIUM_\r\nif \"%DELETE_RESULTS%\"==\"true\" rmdir \"%VIVARIUM_RESULTS_DIR%\"\r\n"
            : "#!/bin/sh\nif [ -d \"$VIVARIUM_RESULTS_DIR\" ]; then echo RESULTS_EXISTS=true; else echo RESULTS_EXISTS=false; fi\nenv | sort | grep '^VIVARIUM_'\nif [ \"$DELETE_RESULTS\" = true ]; then rmdir \"$VIVARIUM_RESULTS_DIR\"; fi\n");

        var assignment = new BuildAssignment { BuildId = "build-environment" };
        assignment.Parameters.Add("cell", "linux-x64");
        assignment.Parameters.Add("suite", "smoke");
        assignment.Steps.Add(CreateStep(scriptPath, deleteResults: true));
        var overridden = CreateStep(scriptPath, deleteResults: false);
        overridden.Env.Add("VIVARIUM_CELL", "step-override");
        assignment.Steps.Add(overridden);

        var log = new StringBuilder();
        using var blobs = new BlobClient("https://localhost", "unused");
        var result = await BuildExecutor.ExecuteAsync(
            root,
            assignment,
            blobs,
            (message, _) =>
            {
                if (message.MsgCase == AgentMsg.MsgOneofCase.Log)
                {
                    lock (log)
                    {
                        log.Append(message.Log.Data.ToStringUtf8());
                    }
                }

                return Task.CompletedTask;
            },
            "session-1",
            CancellationToken.None);

        var workdir = Path.GetFullPath(Path.Combine(root, assignment.BuildId));
        var resultsDir = Path.Combine(workdir, "results");
        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(BuildOutcome.Succeeded));
            Assert.That(result.Steps, Has.Count.EqualTo(2));
            Assert.That(log.ToString(), Does.Contain("RESULTS_EXISTS=true"));
            Assert.That(log.ToString(), Does.Contain($"VIVARIUM_BUILD_ID={assignment.BuildId}"));
            Assert.That(log.ToString(), Does.Contain($"VIVARIUM_WORKDIR={workdir}"));
            Assert.That(log.ToString(), Does.Contain($"VIVARIUM_RESULTS_DIR={resultsDir}"));
            Assert.That(log.ToString(), Does.Contain("VIVARIUM_CELL=linux-x64"));
            Assert.That(log.ToString(), Does.Contain("VIVARIUM_CELL=step-override"));
            Assert.That(log.ToString(), Does.Contain("VIVARIUM_PARAM_CELL=linux-x64"));
            Assert.That(log.ToString(), Does.Contain("VIVARIUM_PARAM_SUITE=smoke"));
            Assert.That(Path.IsPathFullyQualified(resultsDir), Is.True);
            Assert.That(Directory.Exists(resultsDir), Is.True);
        });
    }

    [Test]
    public async Task Execute_resolves_an_existing_bare_program_from_the_step_working_directory()
    {
        var assignment = new BuildAssignment { BuildId = "build-local-program" };
        var workdir = Path.Combine(root, assignment.BuildId);
        Directory.CreateDirectory(workdir);
        var sourceProgram = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")
            : "/bin/sh";
        var localProgram = Path.Combine(
            workdir, OperatingSystem.IsWindows() ? "payload-tool.exe" : "payload-tool");
        File.Copy(sourceProgram, localProgram);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(localProgram,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var step = new Step { Program = Path.GetFileName(localProgram) };
        if (OperatingSystem.IsWindows())
        {
            step.Args.Add("/d");
            step.Args.Add("/c");
        }
        else
        {
            step.Args.Add("-c");
        }
        step.Args.Add("echo PAYLOAD_LOCAL_PROGRAM");
        assignment.Steps.Add(step);

        var log = new StringBuilder();
        using var blobs = new BlobClient("https://localhost", "unused");
        var result = await BuildExecutor.ExecuteAsync(
            root,
            assignment,
            blobs,
            (message, _) =>
            {
                if (message.MsgCase == AgentMsg.MsgOneofCase.Log)
                {
                    lock (log)
                    {
                        log.Append(message.Log.Data.ToStringUtf8());
                    }
                }

                return Task.CompletedTask;
            },
            "session-local-program",
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(BuildOutcome.Succeeded));
            Assert.That(log.ToString(), Does.Contain("PAYLOAD_LOCAL_PROGRAM"));
        });
    }

    private static Step CreateStep(string scriptPath, bool deleteResults)
    {
        var step = new Step
        {
            Program = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
        };
        if (OperatingSystem.IsWindows())
        {
            step.Args.Add("/d");
            step.Args.Add("/c");
        }

        step.Args.Add(scriptPath);
        step.Env.Add("DELETE_RESULTS", deleteResults ? "true" : "false");
        return step;
    }
}
