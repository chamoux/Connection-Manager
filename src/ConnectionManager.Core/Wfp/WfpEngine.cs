using System.Runtime.InteropServices;

namespace ConnectionManager.Core.Wfp;

/// Owns one dynamic WFP session. Its filters live only as long as this object and this process: Windows deletes
/// them when the session closes, including when the process crashes. Needs admin rights.
public sealed class WfpEngine : IWfpEngine
{
    private const ushort SubLayerWeight = 0x4000;
    private const byte LoopbackPermitWeight = 15; // beats every RuleWeight inside our sublayer

    // Outgoing connections and accepted incoming ones, IPv4 and IPv6.
    private static readonly Guid[] Layers =
    [
        WfpNative.LayerAleAuthConnectV4, WfpNative.LayerAleAuthConnectV6,
        WfpNative.LayerAleAuthRecvAcceptV4, WfpNative.LayerAleAuthRecvAcceptV6,
    ];

    private readonly Guid _subLayerKey = Guid.NewGuid(); // fresh per session, so two sessions never collide
    private IntPtr _handle;

    public WfpEngine(string sessionName = "Connection Manager")
    {
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("The WFP structures are laid out for 64-bit processes");

        var name = Marshal.StringToHGlobalUni(sessionName);
        try
        {
            // A 3 s transaction wait: 0 would mean the 15 s default, on the UI thread.
            var session = new FWPM_SESSION0 { displayData = new FWPM_DISPLAY_DATA0 { name = name }, flags = WfpNative.SessionFlagDynamic, txnWaitTimeoutInMSec = 3000 };
            Check("FwpmEngineOpen0", WfpNative.FwpmEngineOpen0(null, WfpNative.RpcCAuthnWinNt, IntPtr.Zero, ref session, out _handle));
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }

        try
        {
            InTransaction(() =>
            {
                AddSubLayer(sessionName);
                // Local traffic (127.0.0.1, ::1) always passes, so blocked apps keep talking to their own helpers.
                foreach (var layer in Layers) AddLoopbackPermit(layer);
            });
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public IReadOnlyList<ulong> Add(FilterRule rule)
    {
        using var condition = FilterCondition.For(rule);
        // The smoke test counts filters by the "block" name.
        var name = $"Connection Manager: {(rule.Action == RuleAction.Block ? "block" : "allow")} {rule.Label}";
        var action = rule.Action == RuleAction.Block ? WfpNative.ActionBlock : WfpNative.ActionPermit;
        var ids = new List<ulong>(Layers.Length);
        try
        {
            foreach (var layer in Layers) ids.Add(AddFilter(layer, name, action, (byte)rule.Weight, condition.Values));
            return ids;
        }
        catch (Exception first)
        {
            // Never leave half a rule in place: nothing would remember to remove it. If the undo itself fails,
            // throw something the reconciler doesn't swallow, so the whole transaction is rolled back instead.
            foreach (var id in ids)
            {
                var code = WfpNative.FwpmFilterDeleteById0(_handle, id);
                if (code != 0 && code != WfpNative.ErrorFilterNotFound)
                    throw new InvalidOperationException($"Could not undo a half-added filter for {rule.Kind} {rule.Value}", first);
            }
            throw;
        }
    }

    public void DeleteFilter(ulong filterId)
    {
        var code = WfpNative.FwpmFilterDeleteById0(_handle, filterId);
        if (code != WfpNative.ErrorFilterNotFound) Check("FwpmFilterDeleteById0", code);
    }

    public void InTransaction(Action work)
    {
        Check("FwpmTransactionBegin0", WfpNative.FwpmTransactionBegin0(_handle, 0));
        try
        {
            work();
        }
        catch
        {
            WfpNative.FwpmTransactionAbort0(_handle);
            throw;
        }
        var commit = WfpNative.FwpmTransactionCommit0(_handle);
        if (commit == 0) return;
        WfpNative.FwpmTransactionAbort0(_handle); // never leave a transaction open: every later begin would fail
        throw new WfpException("FwpmTransactionCommit0", commit);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        WfpNative.FwpmEngineClose0(_handle); // ends the dynamic session: every filter it added is deleted
        _handle = IntPtr.Zero;
    }

    internal static void Check(string operation, uint code)
    {
        if (code != 0) throw new WfpException(operation, code);
    }

    private void AddSubLayer(string name)
    {
        var display = Marshal.StringToHGlobalUni(name);
        try
        {
            var subLayer = new FWPM_SUBLAYER0 { subLayerKey = _subLayerKey, displayData = new FWPM_DISPLAY_DATA0 { name = display }, weight = SubLayerWeight };
            Check("FwpmSubLayerAdd0", WfpNative.FwpmSubLayerAdd0(_handle, ref subLayer, IntPtr.Zero));
        }
        finally
        {
            Marshal.FreeHGlobal(display);
        }
    }

    private void AddLoopbackPermit(Guid layer)
    {
        var loopback = new FWPM_FILTER_CONDITION0
        {
            fieldKey = WfpNative.ConditionFlags,
            matchType = WfpNative.MatchFlagsAllSet,
            conditionValue = new FWP_CONDITION_VALUE0 { type = WfpNative.DataUint32, uint32 = WfpNative.ConditionFlagIsLoopback },
        };
        AddFilter(layer, "Connection Manager: allow loopback", WfpNative.ActionPermit, LoopbackPermitWeight, [loopback]);
    }

    private ulong AddFilter(Guid layer, string name, uint action, byte weight, FWPM_FILTER_CONDITION0[] conditions)
    {
        var display = Marshal.StringToHGlobalUni(name);
        var conditionArray = AllocConditions(conditions);
        try
        {
            var filter = new FWPM_FILTER0
            {
                displayData = new FWPM_DISPLAY_DATA0 { name = display },
                layerKey = layer,
                subLayerKey = _subLayerKey,
                weight = new FWP_VALUE0 { type = WfpNative.DataUint8, uint8 = weight },
                numFilterConditions = (uint)conditions.Length,
                filterCondition = conditionArray,
                action = new FWPM_ACTION0 { type = action },
            };
            Check("FwpmFilterAdd0", WfpNative.FwpmFilterAdd0(_handle, ref filter, IntPtr.Zero, out var id));
            return id;
        }
        finally
        {
            Marshal.FreeHGlobal(conditionArray);
            Marshal.FreeHGlobal(display);
        }
    }

    /// WFP reads the conditions as one contiguous C array. Free the result with Marshal.FreeHGlobal; no conditions gives Zero.
    internal static IntPtr AllocConditions(FWPM_FILTER_CONDITION0[] conditions)
    {
        if (conditions.Length == 0) return IntPtr.Zero;
        var size = Marshal.SizeOf<FWPM_FILTER_CONDITION0>();
        var array = Marshal.AllocHGlobal(size * conditions.Length);
        for (var i = 0; i < conditions.Length; i++) Marshal.StructureToPtr(conditions[i], array + i * size, false);
        return array;
    }
}
