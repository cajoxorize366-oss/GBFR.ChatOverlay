using System.Runtime.InteropServices;

namespace GBFR.ChatOverlay.Native;

internal interface IRelinkPartyMemberSlotResolver
{
    bool TryResolveSlot(uint memberKey, out int memberSlot);
}

internal interface IRelinkPartyMemberSlotNativeApi
{
    bool TryResolveMemberSlot(uint memberKey, out int memberSlot);
}

/// <summary>
/// Maps the opaque member key carried by chat RPC and by manager+0x6C828/0x6C82C
/// through Relink's verified RVA 0x6CD520 resolver.
/// </summary>
internal sealed class RelinkPartyMemberSlotResolver : IRelinkPartyMemberSlotResolver
{
    private readonly IRelinkPartyMemberSlotNativeApi _native;
    private readonly Action<string> _log;
    private int _failureLogged;

    internal RelinkPartyMemberSlotResolver(
        IRelinkPartyMemberSlotNativeApi native,
        Action<string> log)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    internal static IRelinkPartyMemberSlotResolver CreateForCurrentProcess(
        nint moduleBase,
        RelinkChatRvas rvas,
        Action<string>? log = null)
    {
        if (moduleBase == nint.Zero || rvas.SenderSlotResolver <= 0)
            throw new InvalidOperationException("Relink member-key-to-slot resolver RVA is unavailable.");

        return new RelinkPartyMemberSlotResolver(
            new CurrentProcessRelinkPartyMemberSlotNativeApi(moduleBase, rvas),
            log ?? (_ => { }));
    }

    public bool TryResolveSlot(uint memberKey, out int memberSlot)
    {
        memberSlot = -1;
        try
        {
            if (!_native.TryResolveMemberSlot(memberKey, out var candidate) ||
                candidate is < 0 or >= 4)
            {
                LogFailureOnce(memberKey, "the native member-key resolver returned false or an invalid slot");
                return false;
            }

            memberSlot = candidate;
            return true;
        }
        catch (Exception exception)
        {
            memberSlot = -1;
            LogFailureOnce(
                memberKey,
                $"the member-key resolver failed closed with {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private void LogFailureOnce(uint memberKey, string reason)
    {
        if (Interlocked.Exchange(ref _failureLogged, 1) != 0)
            return;

        try
        {
            _log(
                $"Relink member-key resolver could not map key 0x{memberKey:X8}; " +
                $"the stable Player fallback was kept because {reason}. Further failures are suppressed.");
        }
        catch
        {
            // Never allow a logger failure to escape a native receive hook.
        }
    }

    private sealed class CurrentProcessRelinkPartyMemberSlotNativeApi :
        IRelinkPartyMemberSlotNativeApi
    {
        private readonly SenderSlotResolverDelegate _senderSlotResolver;

        internal CurrentProcessRelinkPartyMemberSlotNativeApi(
            nint moduleBase,
            RelinkChatRvas rvas)
        {
            _senderSlotResolver = Marshal.GetDelegateForFunctionPointer<SenderSlotResolverDelegate>(
                moduleBase + rvas.SenderSlotResolver);
        }

        public bool TryResolveMemberSlot(uint memberKey, out int memberSlot) =>
            _senderSlotResolver(memberKey, out memberSlot);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool SenderSlotResolverDelegate(uint memberKey, out int memberSlot);
    }
}
