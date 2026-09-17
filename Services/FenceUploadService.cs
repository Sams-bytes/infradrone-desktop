// Services/FenceUploadService.cs
//
// Uploads the SORA-derived inclusion fence to an ArduPilot vehicle over MAVLink 2.
//
// WHY THIS DRIVES THE HANDSHAKE BY HAND
// Asv.Mavlink 3.4.1's MissionClientEx.Upload() takes no MavMissionType, so it can
// only transfer regular missions. IMissionClient.MissionSetCount() and ClearAll()
// are likewise mission-only. Only WriteMissionIntItem() exposes MissionType.
// So the MISSION_COUNT that opens the transfer is sent directly on the connection
// with MissionType = Fence. Sending it as type Mission would put the autopilot
// into a mission upload and the fence vertices would land in the wrong store.
//
// SEQUENCE (MAVLink mission protocol, fence variant):
//   GCS -> MISSION_COUNT (type=FENCE, count=N)
//   veh -> MISSION_REQUEST (seq=0..N-1)
//   GCS -> MISSION_ITEM_INT (type=FENCE, cmd=5001, param1=N, x/y = deg*1e7)
//   veh -> MISSION_ACK
//
// SCOPE: Cube Orange / Loong 2160 only. The BCube link is MAVLink v1, which
// ArduPilot does not accept fences over; it needs the legacy FENCE_POINT path.
//
// SAFETY:
//  - Upload does NOT enable the fence. FENCE_ENABLE is a separate, deliberate call.
//  - breachAction is required, no default. FenceAction.Report is the conservative
//    choice while validating: the autopilot reports and takes no autonomous action.
//  - Points must come from SoraFenceBuilder, which caps at 70 vertices and proves
//    the polygon lies inside the approved ground risk buffer.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Asv.Mavlink;
using Asv.Mavlink.V2.Common;

namespace InfraDroneDesktop.Services
{
    /// <summary>ArduPilot FENCE_ACTION values.</summary>
    public enum FenceAction
    {
        None = 0,
        Guided = 1,
        Report = 2,
        GuidedThrottlePass = 3,
        ReturnToLaunch = 4,
        Hold = 5,
        Terminate = 6,
        Land = 7
    }

    [Flags]
    public enum FenceType
    {
        None = 0, MaxAltitude = 1, CircleAroundHome = 2, MinAltitude = 4, Polygon = 8
    }

    public sealed class FenceUploadOutcome
    {
        public bool Success { get; init; }
        public string Message { get; init; } = "";
        public int PointsSent { get; init; }
    }

    public sealed class FenceUploadService
    {
        private readonly IMavlinkV2Connection _connection;
        private readonly IMissionClient _mission;
        private readonly IParamsClient? _params;
        private readonly MavlinkClientIdentity _identity;
        private readonly IPacketSequenceCalculator _seq;

        public FenceUploadService(IMavlinkV2Connection connection,
                                  IMissionClient mission,
                                  IParamsClient? paramsClient,
                                  MavlinkClientIdentity identity,
                                  IPacketSequenceCalculator seq)
        {
            _connection = connection;
            _mission = mission;
            _params = paramsClient;
            _identity = identity;
            _seq = seq;
        }

        public async Task<FenceUploadOutcome> UploadAsync(FenceBuildResult fence,
                                                          FenceAction breachAction,
                                                          int timeoutMs = 15000,
                                                          CancellationToken ct = default)
        {
            if (fence == null || fence.Points.Count < 3)
                return Fail("Fence has fewer than 3 points - nothing to upload.");
            if (fence.Points.Count > SoraFenceBuilder.MaxFencePoints)
                return Fail($"Fence has {fence.Points.Count} points, over the " +
                            $"{SoraFenceBuilder.MaxFencePoints} limit. It must be simplified first.");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            var tct = timeoutCts.Token;

            var total = (float)fence.Points.Count;
            var ackTcs = new TaskCompletionSource<MavMissionResult>();

            using var ackSub = _mission.OnMissionAck
                .Subscribe(a => ackTcs.TrySetResult(a.Type));

            // Serve each vertex as the vehicle asks for it.
            using var reqSub = _mission.OnMissionRequest
                .Subscribe(async req =>
                {
                    try
                    {
                        var i = req.Seq;
                        if (i >= fence.Points.Count) return;
                        var (lat, lon) = fence.Points[i];
                        await _mission.WriteMissionIntItem(p =>
                        {
                            p.TargetSystem = _identity.TargetSystemId;
                            p.TargetComponent = _identity.TargetComponentId;
                            p.Seq = i;
                            p.Frame = MavFrame.MavFrameGlobal;
                            p.Command = MavCmd.MavCmdNavFencePolygonVertexInclusion;
                            p.MissionType = MavMissionType.MavMissionTypeFence;
                            p.Current = 0;
                            p.Autocontinue = 1;
                            p.Param1 = total;              // vertex count, same on EVERY point
                            p.Param2 = 0; p.Param3 = 0; p.Param4 = 0;
                            p.X = (int)Math.Round(lat * 1e7);   // degrees * 1e7
                            p.Y = (int)Math.Round(lon * 1e7);
                            p.Z = 0;
                        }, tct);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[Fence] item send failed: " + ex.Message);
                    }
                });

            try
            {
                // Clear any existing fence first: MISSION_COUNT of 0, fence type.
                await SendMissionCountAsync(0, tct);
                await Task.Delay(300, tct);

                await SendMissionCountAsync((ushort)fence.Points.Count, tct);

                var ack = await ackTcs.Task.WaitAsync(tct);
                if (ack != MavMissionResult.MavMissionAccepted)
                    return Fail($"Vehicle rejected the fence: {ack}");

                if (_params != null)
                {
                    // ArduPilot exposes FENCE_* as INT32-typed parameters.
                    await _params.Write("FENCE_ACTION", MavParamType.MavParamTypeInt32,
                                        (float)(int)breachAction, tct);

                    // Read-modify-write so an existing altitude or circle fence the
                    // operator configured is not silently dropped.
                    var current = await _params.Read("FENCE_TYPE", tct);
                    var currentType = (int)current.ParamValue;
                    var newType = currentType | (int)FenceType.Polygon;
                    if (newType != currentType)
                        await _params.Write("FENCE_TYPE", MavParamType.MavParamTypeInt32,
                                            newType, tct);
                }

                return new FenceUploadOutcome
                {
                    Success = true,
                    PointsSent = fence.Points.Count,
                    Message = $"Fence uploaded: {fence.Points.Count} points, breach action " +
                              $"{breachAction}. NOT yet enabled - enable it separately."
                };
            }
            catch (OperationCanceledException)
            {
                return Fail("Fence upload timed out. Nothing was enabled.");
            }
            catch (Exception ex)
            {
                return Fail("Fence upload failed: " + ex.Message);
            }
        }

        /// <summary>
        /// MISSION_COUNT with MissionType = Fence - the packet the library's own
        /// MissionSetCount cannot send, since it hardcodes the mission type.
        /// </summary>
        private async Task SendMissionCountAsync(ushort count, CancellationToken ct)
        {
            var pkt = new MissionCountPacket
            {
                SystemId = _identity.SystemId,
                ComponentId = _identity.ComponentId,
                Sequence = _seq.GetNextSequenceNumber()
            };
            pkt.Payload.TargetSystem = _identity.TargetSystemId;
            pkt.Payload.TargetComponent = _identity.TargetComponentId;
            pkt.Payload.Count = count;
            pkt.Payload.MissionType = MavMissionType.MavMissionTypeFence;
            await _connection.Send(pkt, ct);
        }

        /// <summary>
        /// Turns the fence on or off. Separate from upload on purpose, so a fence can
        /// be loaded and read back before it is made live.
        /// </summary>
        public async Task<FenceUploadOutcome> SetEnabledAsync(bool enabled, CancellationToken ct = default)
        {
            if (_params == null) return Fail("No params client - cannot change FENCE_ENABLE.");
            try
            {
                await _params.Write("FENCE_ENABLE", MavParamType.MavParamTypeInt32,
                                    enabled ? 1f : 0f, ct);
                return new FenceUploadOutcome
                {
                    Success = true,
                    Message = enabled ? "Fence ENABLED on vehicle." : "Fence disabled on vehicle."
                };
            }
            catch (Exception ex) { return Fail("Could not change FENCE_ENABLE: " + ex.Message); }
        }

        /// <summary>
        /// Reads the fence back so it can be compared against what was sent.
        /// Worth doing before the first flight with any new fence.
        /// </summary>
        public async Task<IReadOnlyList<(double lat, double lon)>> DownloadAsync(CancellationToken ct = default)
        {
            var count = await _mission.MissionRequestCount(ct);
            var pts = new List<(double, double)>();
            for (ushort i = 0; i < count; i++)
            {
                var item = await _mission.MissionRequestItem(i, ct);
                if (item.Command == MavCmd.MavCmdNavFencePolygonVertexInclusion)
                    pts.Add((item.X / 1e7, item.Y / 1e7));
            }
            return pts;
        }

        private static FenceUploadOutcome Fail(string msg) =>
            new FenceUploadOutcome { Success = false, Message = msg };
    }
}
