// Services/FenceUploadService.cs
//
// Uploads the SORA-derived inclusion fence to an ArduPilot vehicle over MAVLink 2,
// reads it back, and turns on polygon enforcement.
//
// HOW IT TALKS TO THE VEHICLE
// Asv.Mavlink 3.4.1's MissionClient helpers only handle regular missions (no
// MissionType), so the fence handshake is driven by hand on the raw connection,
// the same proven pattern as AsvMavLinkService.UploadMissionAsync. Every packet
// is filtered on the target system ID AND MissionType = Fence, so a mission
// transfer or another ground station on the link can never be mistaken for it.
//
//   Upload:    GCS MISSION_COUNT(fence, N)
//              veh MISSION_REQUEST / MISSION_REQUEST_INT(fence, seq)  x N
//              GCS MISSION_ITEM_INT(fence, cmd 5001, param1 = N, x/y = deg*1e7)
//              veh MISSION_ACK(fence)
//   Read-back: GCS MISSION_REQUEST_LIST(fence) -> veh MISSION_COUNT(fence)
//              GCS MISSION_REQUEST_INT(fence, seq) -> veh MISSION_ITEM_INT  x N
//              GCS MISSION_ACK(fence, accepted)
//
// A new MISSION_COUNT replaces the vehicle's whole fence store, so no separate
// "clear" is sent. (The previous version sent a clear first; ArduPilot ACKs that
// clear as ACCEPTED, and that ACK was mistaken for the upload's - reporting
// success before a single point was sent.)
//
// PARAMETERS (values from ArduPilot source, libraries/AC_Fence/AC_Fence.h):
//   FENCE_TYPE bits: 1 = max altitude, 2 = circle, 4 = POLYGON, 8 = min altitude.
//   After a verified upload the polygon bit (4) is added, read-modify-write, so
//   the uploaded polygon is actually enforced. Nothing else is changed:
//   FENCE_ACTION is left exactly as the operator set it (the Pre-flight failsafe
//   check grades it), and FENCE_ENABLE is never touched here.
//
// SCOPE: Cube Orange / Loong 2160 only. The BCube link is MAVLink v1, which
// ArduPilot does not accept fences over.
//
// SAFETY: the caller must refuse to upload while armed (FlightView does). Points
// must come from SoraFenceBuilder, which caps at 70 vertices and proves the
// polygon lies inside the approved ground risk buffer.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Asv.Mavlink;
using Asv.Mavlink.V2.Common;

namespace InfraDroneDesktop.Services
{
    public sealed class FenceUploadOutcome
    {
        public bool Success { get; init; }
        public string Message { get; init; } = "";
        public int PointsSent { get; init; }
    }

    public sealed class FenceUploadService
    {
        /// <summary>FENCE_TYPE bit for polygon fences (AC_FENCE_TYPE_POLYGON).</summary>
        public const int FenceTypePolygonBit = 4;

        private readonly IMavlinkV2Connection _connection;
        private readonly IParamsClient? _params;
        private readonly MavlinkClientIdentity _identity;
        private readonly IPacketSequenceCalculator _seq;

        public FenceUploadService(IMavlinkV2Connection connection,
                                  IParamsClient? paramsClient,
                                  MavlinkClientIdentity identity,
                                  IPacketSequenceCalculator seq)
        {
            _connection = connection;
            _params = paramsClient;
            _identity = identity;
            _seq = seq;
        }

        // ------------------------------------------------------------------
        // Upload
        // ------------------------------------------------------------------
        public async Task<FenceUploadOutcome> UploadAsync(FenceBuildResult fence,
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

            var count = (ushort)fence.Points.Count;
            var ackTcs = new TaskCompletionSource<MavMissionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var served = new HashSet<ushort>();

            async Task SendVertex(ushort seq)
            {
                if (seq >= count) return;
                lock (served) served.Add(seq);
                var (lat, lon) = fence.Points[seq];
                var pkt = new MissionItemIntPacket
                {
                    SystemId = _identity.SystemId,
                    ComponentId = _identity.ComponentId,
                    Sequence = _seq.GetNextSequenceNumber()
                };
                var p = pkt.Payload;
                p.TargetSystem = _identity.TargetSystemId;
                p.TargetComponent = _identity.TargetComponentId;
                p.Seq = seq;
                p.Frame = MavFrame.MavFrameGlobal;
                p.Command = MavCmd.MavCmdNavFencePolygonVertexInclusion;
                p.MissionType = MavMissionType.MavMissionTypeFence;
                p.Current = 0;
                p.Autocontinue = 1;
                p.Param1 = count;          // vertex count of this polygon, same on EVERY point
                p.Param2 = 0; p.Param3 = 0; p.Param4 = 0;
                p.X = (int)Math.Round(lat * 1e7);
                p.Y = (int)Math.Round(lon * 1e7);
                p.Z = 0;
                await _connection.Send(pkt, tct);
            }

            using var sub = _connection.Subscribe(packet =>
            {
                if (packet.SystemId != _identity.TargetSystemId) return;
                try
                {
                    switch (packet.Payload)
                    {
                        case MissionRequestIntPayload rqi when rqi.MissionType == MavMissionType.MavMissionTypeFence:
                            _ = SendVertex(rqi.Seq);
                            break;
                        case MissionRequestPayload rq when rq.MissionType == MavMissionType.MavMissionTypeFence:
                            _ = SendVertex(rq.Seq);
                            break;
                        case MissionAckPayload ack when ack.MissionType == MavMissionType.MavMissionTypeFence:
                            ackTcs.TrySetResult(ack.Type);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Fence] vertex send failed: " + ex.Message);
                }
            });

            try
            {
                await SendCountAsync(count, tct);
                var result = await ackTcs.Task.WaitAsync(tct);
                int n; lock (served) n = served.Count;

                if (result != MavMissionResult.MavMissionAccepted)
                    return Fail($"Vehicle rejected the fence: {result}.");
                if (n < count)
                    return Fail($"Vehicle acknowledged after only {n} of {count} points - treat as failed and upload again.");

                return new FenceUploadOutcome
                {
                    Success = true,
                    PointsSent = count,
                    Message = $"Fence accepted by the vehicle: {count} points."
                };
            }
            catch (OperationCanceledException)
            {
                int n; lock (served) n = served.Count;
                return Fail($"Fence upload timed out ({n} of {count} points requested by the vehicle). Nothing was enabled.");
            }
            catch (Exception ex)
            {
                return Fail("Fence upload failed: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Read-back: downloads the FENCE store (not the mission)
        // ------------------------------------------------------------------
        public async Task<IReadOnlyList<(double lat, double lon)>> DownloadAsync(int timeoutMs = 15000,
                                                                               CancellationToken ct = default)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            var tct = timeoutCts.Token;

            var countTcs = new TaskCompletionSource<ushort>(TaskCreationOptions.RunContinuationsAsynchronously);
            var items = new Dictionary<ushort, MissionItemIntPayload>();
            TaskCompletionSource<bool>? itemTcs = null;
            ushort waitingFor = 0;

            using var sub = _connection.Subscribe(packet =>
            {
                if (packet.SystemId != _identity.TargetSystemId) return;
                switch (packet.Payload)
                {
                    case MissionCountPayload c when c.MissionType == MavMissionType.MavMissionTypeFence:
                        countTcs.TrySetResult(c.Count);
                        break;
                    case MissionItemIntPayload it when it.MissionType == MavMissionType.MavMissionTypeFence:
                        lock (items) items[it.Seq] = it;
                        if (it.Seq == waitingFor) itemTcs?.TrySetResult(true);
                        break;
                }
            });

            // Ask for the count (retry once - UDP can drop a packet).
            ushort total = 0;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var list = new MissionRequestListPacket
                {
                    SystemId = _identity.SystemId,
                    ComponentId = _identity.ComponentId,
                    Sequence = _seq.GetNextSequenceNumber()
                };
                list.Payload.TargetSystem = _identity.TargetSystemId;
                list.Payload.TargetComponent = _identity.TargetComponentId;
                list.Payload.MissionType = MavMissionType.MavMissionTypeFence;
                await _connection.Send(list, tct);
                var done = await Task.WhenAny(countTcs.Task, Task.Delay(2500, tct));
                if (done == countTcs.Task) { total = countTcs.Task.Result; break; }
            }
            if (!countTcs.Task.IsCompleted)
                throw new TimeoutException("Vehicle did not report its fence size.");

            var pts = new List<(double, double)>();
            for (ushort i = 0; i < total; i++)
            {
                bool got = false;
                for (int attempt = 0; attempt < 3 && !got; attempt++)
                {
                    waitingFor = i;
                    itemTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (items) if (items.ContainsKey(i)) { got = true; break; }

                    var req = new MissionRequestIntPacket
                    {
                        SystemId = _identity.SystemId,
                        ComponentId = _identity.ComponentId,
                        Sequence = _seq.GetNextSequenceNumber()
                    };
                    req.Payload.TargetSystem = _identity.TargetSystemId;
                    req.Payload.TargetComponent = _identity.TargetComponentId;
                    req.Payload.Seq = i;
                    req.Payload.MissionType = MavMissionType.MavMissionTypeFence;
                    await _connection.Send(req, tct);
                    var done = await Task.WhenAny(itemTcs.Task, Task.Delay(1500, tct));
                    got = done == itemTcs.Task;
                }
                if (!got) throw new TimeoutException($"Vehicle did not send fence point {i}.");

                MissionItemIntPayload item;
                lock (items) item = items[i];
                if (item.Command == MavCmd.MavCmdNavFencePolygonVertexInclusion)
                    pts.Add((item.X / 1e7, item.Y / 1e7));
            }

            // Close the transfer politely so the vehicle doesn't keep waiting.
            var ackPkt = new MissionAckPacket
            {
                SystemId = _identity.SystemId,
                ComponentId = _identity.ComponentId,
                Sequence = _seq.GetNextSequenceNumber()
            };
            ackPkt.Payload.TargetSystem = _identity.TargetSystemId;
            ackPkt.Payload.TargetComponent = _identity.TargetComponentId;
            ackPkt.Payload.Type = MavMissionResult.MavMissionAccepted;
            ackPkt.Payload.MissionType = MavMissionType.MavMissionTypeFence;
            await _connection.Send(ackPkt, tct);

            return pts;
        }

        // ------------------------------------------------------------------
        // Polygon enforcement: FENCE_TYPE |= 4 (read-modify-write)
        // ------------------------------------------------------------------
        /// <summary>
        /// Adds the polygon bit to FENCE_TYPE so the uploaded polygon is enforced,
        /// keeping any altitude/circle fences the operator configured. Returns the
        /// before/after values for the status line.
        /// </summary>
        public async Task<(bool Ok, string Message)> EnablePolygonTypeAsync(CancellationToken ct = default)
        {
            if (_params == null) return (false, "No parameter link - set FENCE_TYPE bit 4 in QGroundControl.");
            try
            {
                var current = await _params.Read("FENCE_TYPE", ct);
                int before = (int)Math.Round(current.ParamValue);
                int after = before | FenceTypePolygonBit;
                if (after == before)
                    return (true, $"FENCE_TYPE already includes polygons ({before}).");
                // ArduPilot converts the float to the parameter's own integer type.
                await _params.Write("FENCE_TYPE", MavParamType.MavParamTypeReal32, after, ct);
                var check = await _params.Read("FENCE_TYPE", ct);
                int now = (int)Math.Round(check.ParamValue);
                return now == after
                    ? (true, $"FENCE_TYPE {before} -> {after} (polygon fence now enforced).")
                    : (false, $"Tried to set FENCE_TYPE to {after}, vehicle reports {now}. Set it in QGroundControl.");
            }
            catch (Exception ex)
            {
                return (false, "Could not change FENCE_TYPE: " + ex.Message + ". Set bit 4 in QGroundControl.");
            }
        }

        private async Task SendCountAsync(ushort count, CancellationToken ct)
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

        private static FenceUploadOutcome Fail(string msg) =>
            new FenceUploadOutcome { Success = false, Message = msg };
    }
}
