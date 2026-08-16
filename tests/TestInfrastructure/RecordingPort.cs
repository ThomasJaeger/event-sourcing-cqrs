using System.Reflection;
using System.Runtime.ExceptionServices;

namespace EventSourcingCqrs.TestInfrastructure;

// The recording half of ADR 0031's write-surface coverage property, for any port rather than for
// one family's. Wrap returns a proxy implementing the port interface whose every member forwards
// to the real adapter and records the member it forwarded, so the recorded run is the real run:
// the proxy observes, it does not stand in. A proxy that answered on its own behalf would turn the
// isolation property into an assertion about the proxy.
//
// A member is recorded as its declaring interface's name and its own, joined by a dot, and
// CrossTenantCoverage.DeclaredWrites builds the same token. A family wires two ports together and
// they record into one set, so bare method names would let a read on the store earn coverage for a
// same-named write on the unit of work. Qualifying removes that class of agreement rather than
// relying on the two ports never colliding.
//
// The cascade list is how a store proxy reaches the unit of work it hands out. A member returning
// a cascaded interface, directly or as a Task of one, has its result wrapped in turn, so BeginAsync
// yields a recording unit of work without anything here naming one.
public static class RecordingPort
{
    public static TPort Wrap<TPort>(TPort inner, ISet<string> invoked, params Type[] cascade)
        where TPort : class
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(invoked);
        ArgumentNullException.ThrowIfNull(cascade);
        return (TPort)WrapAs(typeof(TPort), inner, invoked, cascade);
    }

    internal static object WrapAs(Type port, object inner, ISet<string> invoked, Type[] cascade)
    {
        var proxy = DispatchProxy.Create(port, typeof(RecordingProxy));
        ((RecordingProxy)proxy).Attach(inner, invoked, cascade);
        return proxy;
    }
}

// Not sealed: DispatchProxy.Create builds a type deriving from this one.
public class RecordingProxy : DispatchProxy
{
    private object _inner = null!;
    private ISet<string> _invoked = null!;
    private Type[] _cascade = null!;

    internal void Attach(object inner, ISet<string> invoked, Type[] cascade)
    {
        _inner = inner;
        _invoked = invoked;
        _cascade = cascade;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        _invoked.Add($"{targetMethod.DeclaringType!.Name}.{targetMethod.Name}");

        object? result;
        try
        {
            result = targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Rethrow what the adapter threw, with its stack, so a real failure surfaces as
            // itself rather than as a reflection wrapper around itself.
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }

        return Cascade(targetMethod.ReturnType, result);
    }

    private object? Cascade(Type returnType, object? result)
    {
        if (result is null)
        {
            return null;
        }

        var direct = Array.Find(_cascade, t => t.IsInstanceOfType(result));
        if (direct is not null)
        {
            return RecordingPort.WrapAs(direct, result, _invoked, _cascade);
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var awaited = returnType.GetGenericArguments()[0];
            var port = Array.Find(_cascade, t => t.IsAssignableFrom(awaited));
            if (port is not null)
            {
                return typeof(RecordingProxy)
                    .GetMethod(nameof(WrapAwaitedAsync), BindingFlags.NonPublic | BindingFlags.Instance)!
                    .MakeGenericMethod(awaited)
                    .Invoke(this, [result, port])!;
            }
        }

        return result;
    }

    private async Task<T> WrapAwaitedAsync<T>(Task<T> task, Type port)
        where T : class
        => (T)RecordingPort.WrapAs(port, await task, _invoked, _cascade);
}
