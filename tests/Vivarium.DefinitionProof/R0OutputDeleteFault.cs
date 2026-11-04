using System.Reflection;
using System.Runtime.ExceptionServices;
using EyeAuras.Storage;
using EyeAuras.Storage.Contracts;

namespace Vivarium.DefinitionProof;

internal sealed class R0OutputDeleteFault
{
    internal volatile bool Reject = true;
    internal IObjectStorage Wrap(IObjectStorage storage)
    {
        var result = DispatchProxy.Create<IObjectStorage, Proxy>();
        var proxy = (Proxy)(object)result; proxy.Storage = storage; proxy.Fault = this; return result;
    }

    public class Proxy : DispatchProxy
    {
        internal IObjectStorage Storage = null!;
        internal R0OutputDeleteFault Fault = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IObjectStorage.DeleteAsync) && Fault.Reject)
                return Task.FromException<ObjectDeleted>(new IOException("Owned private deletion fault"));
            try { return method.Invoke(Storage, args); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
    }
}
