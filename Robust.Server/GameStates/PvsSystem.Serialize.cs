using System;
using System.Threading.Tasks;
using Prometheus;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Player;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Robust.Server.GameStates;

internal sealed partial class PvsSystem
{
    [Dependency] private IRobustSerializer _serializer = default!;

    /// <summary>
    /// Get and serialize <see cref="GameState"/> objects for each player. Compressing & sending the states is done later.
    /// </summary>
    private void SerializeStates()
    {
        using var _ = Histogram.WithLabels("Serialize States").NewTimer();
        var opts = new ParallelOptions {MaxDegreeOfParallelism = _parallelMgr.ParallelProcessCount};
        _oldestAck = GameTick.MaxValue.Value;
        Parallel.For(-1, _sessions.Length, opts, SerializeState);
    }

    /// <summary>
    /// Get and serialize a <see cref="GameState"/> for a single session (or the current replay).
    /// </summary>
    private void SerializeState(int i)
    {
        try
        {
            var guid = i >= 0 ? _sessions[i].Session.UserId.UserId : default;
            ServerGameStateManager.PvsEventSource.Log.WorkStart(_gameTiming.CurTick.Value, i, guid);

            if (i >= 0)
                SerializeSessionState(_sessions[i]);
            else
                _replay.Update();

            ServerGameStateManager.PvsEventSource.Log.WorkStop(_gameTiming.CurTick.Value, i, guid);
        }
        catch (Exception e) // Catch EVERY exception
        {
            var source = i >= 0 ? _sessions[i].Session.ToString() : "replays";
            Log.Log(LogLevel.Error, e, $"Caught exception while serializing game state for {source}.");
#if !EXCEPTION_TOLERANCE
            throw;
#endif
        }
    }

    /// <summary>
    /// Get and serialize a <see cref="GameState"/> for a single session.
    /// </summary>
    private void SerializeSessionState(PvsSession data)
    {
        // _Duty: ClearState MUST run even if ComputeSessionState/serialization throws. Otherwise the session's
        // per-tick buffers (States/State/PlayerStates/Chunks) stay dirty and every subsequent tick fails on
        // UpdateSession's opening asserts -> a self-sustaining cascade (debug: assert spam + crash; release:
        // States accumulates and stale entity states linger, e.g. an aghost-picked item never un-sticks on
        // other clients). A finally makes a single bad tick self-heal instead of poisoning all following ticks.
        try
        {
            ComputeSessionState(data);
            InterlockedHelper.Min(ref _oldestAck, data.FromTick.Value);
            DebugTools.AssertEqual(data.StateStream, null);

            // PVS benchmarks use dummy sessions.
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            if (data.Session.Channel is not DummyChannel)
            {
                data.StateStream = RobustMemoryManager.GetMemoryStream();
                _serializer.SerializeDirect(data.StateStream, data.State);
            }
        }
        finally
        {
            // ClearState resets States/State/PlayerStates/Chunks, but NOT the pooled ToSend list (normally nulled
            // inside GetEntityStates once it is handed off to PreviouslySent). If GetEntityStates threw before that
            // handoff, ToSend is still non-null here -> release it back to the pool and null it, otherwise next
            // tick's UpdateSession asserts (ToSend == null) and the whole session cascades forever.
            if (data.ToSend != null)
            {
                _entDataListPool.Return(data.ToSend);
                data.ToSend = null;
            }
            data.ClearState();
        }
    }
}
