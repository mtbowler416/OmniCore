﻿using OmniCore.Model;
using OmniCore.Model.Enums;
using OmniCore.Model.Eros;
using OmniCore.Model.Exceptions;
using OmniCore.Model.Interfaces;
using OmniCore.Model.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace OmniCore.Radio.RileyLink
{
    public class RileyLinkMessageExchange : IMessageExchange
    {
        private ErosPod Pod;
        private RileyLink RileyLink;
        public RadioPacket LastReceivedPacket;
        public int LastPacketSent = 0;

        private ErosMessageExchangeParameters MessageExchangeParameters;
        private Task FinalAckTask = null;
        private SynchronizationContext UiContext;

        internal RileyLinkMessageExchange(IMessageExchangeParameters messageExchangeParameters, IPod pod, RileyLink rileyLinkInstance,
            SynchronizationContext uiContext)
        {
            UiContext = uiContext;
            SetParameters(messageExchangeParameters, pod, rileyLinkInstance);
        }

        public void SetParameters(IMessageExchangeParameters messageExchangeParameters, IPod pod, RileyLink rileyLinkInstance)
        {
            RileyLink = rileyLinkInstance;
            Pod = pod as ErosPod;
            MessageExchangeParameters = messageExchangeParameters as ErosMessageExchangeParameters;
        }

        private async Task RunInUiContext(Func<Task> a)
        {
            var current = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(UiContext);
            await a.Invoke();
            SynchronizationContext.SetSynchronizationContext(current);
        }

        private async Task<T> RunInUiContext<T>(Func<Task<T>> a)
        {
            var current = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(UiContext);
            var ret = await a.Invoke();
            SynchronizationContext.SetSynchronizationContext(current);
            return ret;
        }


        public async Task InitializeExchange(IMessageExchangeProgress messageProgress)
        {
            messageProgress.Result.Statistics = new RileyLinkStatistics();
            if (FinalAckTask != null)
            {
                messageProgress.ActionText = "Waiting for previous radio operation to complete";
                await FinalAckTask;
            }

            await RunInUiContext(async () => await RileyLink.EnsureDevice(messageProgress));
        }

        public async Task<IMessage> GetResponse(IMessage requestMessage, IMessageExchangeProgress messageExchangeProgress)
        {
            return await GetResponseInternal(requestMessage, messageExchangeProgress);
        }

        private async Task<IMessage> GetResponseInternal(IMessage requestMessage, IMessageExchangeProgress messageProgress)
        {
            try
            {
                ((RileyLinkStatistics)messageProgress.Result.Statistics).StartMessageExchange();
                if (MessageExchangeParameters.TransmissionLevelOverride.HasValue)
                {
                    await RunInUiContext(async () => await RileyLink.SetTxLevel(messageProgress, MessageExchangeParameters.TransmissionLevelOverride.Value));
                }

                RadioPacket received = null;
                bool sendMessage = true;
                while (sendMessage)
                {
                    sendMessage = false;
                    var erosRequestMessage = requestMessage as ErosMessage;
                    var packets = GetRadioPackets(erosRequestMessage);
                    var packetCount = packets.Count;
                    try
                    {
                        for (int packetIndex = 0; packetIndex < packetCount; packetIndex++)
                        {
                            var packetToSend = packets[packetIndex];
                            ((RileyLinkStatistics)messageProgress.Result.Statistics).StartPacketExchange();
                            if (packetIndex == 0)
                                received = await ExchangePacketWithRetries(messageProgress, packetToSend, packetIndex == packetCount - 1 ? PacketType.POD : PacketType.ACK, 15000);
                            else
                                received = await ExchangePacketWithRetries(messageProgress, packetToSend, packetIndex == packetCount - 1 ? PacketType.POD : PacketType.ACK, 30000);

                            ((RileyLinkStatistics)messageProgress.Result.Statistics).EndPacketExchange();
                            this.Pod.RuntimeVariables.PacketSequence = (received.Sequence + 1) % 32;
                        }
                    }
                    catch(OmniCoreProtocolException ocp)
                    {
                        //if (ocp.FailureType == FailureType.AlreadyExecuted)
                        //{
                        //    Pod.RuntimeVariables.PacketSequence--;
                        //    Pod.RuntimeVariables.PacketSequence--;

                        //    if (MessageExchangeParameters.MessageSequenceOverride.HasValue)
                        //    {
                        //        MessageExchangeParameters.MessageSequenceOverride -= 1;
                        //    }
                        //    else
                        //    {
                        //        if (Pod.Status == null)
                        //            MessageExchangeParameters.MessageSequenceOverride = 0xF;
                        //        else
                        //            Pod.Status.MessageSequence -= 1;
                        //    }
                        //    sendMessage = true;
                        //}
                        //else
                            throw;
                    }
                    catch(Exception)
                    {
                        throw;
                    }
                }

                var responseBuilder = new ErosResponseBuilder();

                var messageAddress = MessageExchangeParameters.AddressOverride ?? Pod.RadioAddress;
                var ackAddress = MessageExchangeParameters.AckAddressOverride ?? Pod.RadioAddress;

                while (!responseBuilder.WithRadioPacket(received))
                {
                    ((RileyLinkStatistics)messageProgress.Result.Statistics).StartPacketExchange();
                    var ackPacket = CreateAckPacket(messageAddress, ackAddress, (received.Sequence + 1) % 32);
                    received = await ExchangePacketWithRetries(messageProgress, ackPacket, PacketType.CON, 30000);
                    ((RileyLinkStatistics)messageProgress.Result.Statistics).EndPacketExchange();
                }

                ((RileyLinkStatistics)messageProgress.Result.Statistics).EndMessageExchange();
                var podResponse = responseBuilder.Build();

                Debug.WriteLine($"RCVD MSG {podResponse}");
                Debug.WriteLine("Send and receive completed.");
                Pod.RuntimeVariables.PacketSequence = (received.Sequence + 1) % 32;

                RadioPacket finalAckPacket;
                if (messageAddress == ackAddress)
                    finalAckPacket = CreateAckPacket(messageAddress, 0, Pod.RuntimeVariables.PacketSequence);
                else
                    finalAckPacket = CreateAckPacket(messageAddress, ackAddress, Pod.RuntimeVariables.PacketSequence);

                FinalAckTask = Task.Run(() => AcknowledgeEndOfMessage(finalAckPacket));
                return podResponse;
            }
            catch (Exception)
            {
                ((RileyLinkStatistics)messageProgress.Result.Statistics).ExitPrematurely();
                throw;
            }
        }

        private async Task<RadioPacket> ExchangePacketWithRetries(IMessageExchangeProgress messageProgress, RadioPacket packetToSend,
            PacketType expectedPacketType, int exchangeTimeout)
        {
            int timeoutCount = 0;
            int radioErrorCount = 0;
            int protocolErrorCount = 0;
            int exchangeStart = Environment.TickCount;
            while (exchangeStart + exchangeTimeout > Environment.TickCount)
            {
                packetToSend = packetToSend.WithSequence(this.Pod.RuntimeVariables.PacketSequence);
                try
                {
                    var receivedPacket = await this.ExchangePackets(messageProgress, packetToSend, expectedPacketType);
                    ((RileyLinkStatistics)messageProgress.Result.Statistics).PacketSent(packetToSend);
                    if (receivedPacket != null)
                        return receivedPacket;
                }
                catch (OmniCoreTimeoutException ote)
                {
                    timeoutCount++;
                    radioErrorCount = 0;
                    ((RileyLinkStatistics)messageProgress.Result.Statistics).TimeoutOccured(ote);
                    await HandleTimeoutException(ote, messageProgress, timeoutCount);
                }
                catch (OmniCoreRadioException pre)
                {
                    timeoutCount = 0;
                    radioErrorCount++;
                    ((RileyLinkStatistics)messageProgress.Result.Statistics).RadioErrorOccured(pre);
                    await HandleRadioException(pre, messageProgress, radioErrorCount);
                }
                catch (OmniCoreErosException pe)
                {
                    timeoutCount = 0;
                    radioErrorCount = 0;
                    protocolErrorCount++;
                    ((RileyLinkStatistics)messageProgress.Result.Statistics).ProtocolErrorOccured(pe);
                    await HandleProtocolException(pe, messageProgress, protocolErrorCount);
                }
                catch (Exception e)
                {
                    ((RileyLinkStatistics)messageProgress.Result.Statistics).UnknownErrorOccured(e);
                    throw;
                }
            }
            throw new OmniCoreTimeoutException(FailureType.CommunicationInterrupted);
        }

        private async Task HandleTimeoutException(OmniCoreTimeoutException ote, IMessageExchangeProgress messageProgress, int timeoutCount)
        {
            ((RileyLinkStatistics)messageProgress.Result.Statistics).NoPacketReceived();
            Debug.WriteLine("RECV PKT None");

            if (timeoutCount %3 == 0)
                await RunInUiContext(async () => await RileyLink.TxLevelUp(messageProgress));

            if (timeoutCount == 10)
                await Task.Delay(2000);

            if (timeoutCount == 15)
                await RunInUiContext(async () => await RileyLink.Reset(messageProgress));

            if (timeoutCount == 25)
                await Task.Delay(2000);

            if (timeoutCount == 30)
                Pod.RuntimeVariables.PacketSequence = 0;

            if (timeoutCount > 40)
                throw ote;
        }

        private async Task HandleRadioException(OmniCoreRadioException pre, IMessageExchangeProgress messageProgress, int radioErrorCount)
        {
            if (radioErrorCount % 2 == 1)
                await RunInUiContext(async () => await RileyLink.Reset(messageProgress));

            if (radioErrorCount == 6)
                Pod.RuntimeVariables.PacketSequence = 0;

            if (radioErrorCount > 10)
                throw pre;
        }

        private async Task HandleProtocolException(OmniCoreErosException pe, IMessageExchangeProgress messageProgress, int protocolErrorCount)
        {
            //if (protocolErrorCount < 5 && pe.ReceivedPacket != null && pe.ExpectedType.HasValue)
            //{
            //    if (pe.ReceivedPacket.Type == PacketType.ACK)
            //    {
            //        //await FindOutWhat(messageProgress);
            //        //Pod.Status.MessageSequence--;
            //        throw new OmniCoreProtocolException(FailureType.AlreadyExecuted);
            //    }
            //}
            throw pe;
        }

        //private async Task FindOutWhat(IMessageExchangeProgress progress)
        //{
        //    var messageAddress = MessageExchangeParameters.AddressOverride ?? Pod.RadioAddress;
        //    var ackAddress = MessageExchangeParameters.AckAddressOverride ?? Pod.RadioAddress;
        //    var ackPacket = CreateAckPacket(messageAddress, ackAddress, this.Pod.RuntimeVariables.PacketSequence);
        //    try
        //    {
        //        var received = await ExchangePacketWithRetries(progress, ackPacket, PacketType.POD, 10000);
        //        Debug.WriteLine("YADA!!");
        //    }
        //    catch (OmniCoreTimeoutException)
        //    {
        //        this.Pod.RuntimeVariables.PacketSequence++;
        //        return;
        //        this.Pod.RuntimeVariables.PacketSequence++;
        //        await FindOutWhat(progress);
        //    }
        //}

        private async Task AcknowledgeEndOfMessage(RadioPacket ackPacket)
        {
            try
            {
                Debug.WriteLine("Sending final ack");
                await SendPacket(null, ackPacket);
                Debug.WriteLine("Message exchange finalized");
            }
            catch(Exception)
            {
                Debug.WriteLine("Final ack failed, ignoring.");
            }
        }


        private async Task<RadioPacket> ExchangePackets(IMessageExchangeProgress messageProgress,  
            RadioPacket packet_to_send, PacketType expected_type)
        {
            Bytes receivedData = null;
            Debug.WriteLine($"SEND PKT {packet_to_send}");
            if (this.LastPacketSent == 0 || (Environment.TickCount - this.LastPacketSent) > 2000)
                receivedData = await RunInUiContext(async () => await RileyLink.SendAndGetPacket(messageProgress, packet_to_send.GetPacketData(), 0, 0, 300, 1, 300));
            else
                receivedData = await RunInUiContext(async () => await RileyLink.SendAndGetPacket(messageProgress, packet_to_send.GetPacketData(), 0, 0, 120, 0, 40));

            var receivedPacket = this.GetPacket(receivedData);
            if (receivedPacket == null)
            {
                ((RileyLinkStatistics)messageProgress.Result.Statistics).BadDataReceived(receivedData);
                if (MessageExchangeParameters.AllowAutoLevelAdjustment)
                    await RunInUiContext(async () => await RileyLink.TxLevelDown(messageProgress));
                return null;
            }

            Debug.WriteLine($"RECV PKT {receivedPacket}");
            if (receivedPacket.Address != this.Pod.RadioAddress && receivedPacket.Address != 0xFFFFFFFF)
            {
                ((RileyLinkStatistics)messageProgress.Result.Statistics).BadPacketReceived(receivedPacket);
                Debug.WriteLine("RECV PKT ADDR MISMATCH");
                if (MessageExchangeParameters.AllowAutoLevelAdjustment)
                    await RunInUiContext(async () => await RileyLink.TxLevelDown(messageProgress));
                return null;
            }

            this.LastPacketSent = Environment.TickCount;

            if (this.LastReceivedPacket != null && receivedPacket.Sequence == this.LastReceivedPacket.Sequence
                && receivedPacket.Type == this.LastReceivedPacket.Type)
            {
                ((RileyLinkStatistics)messageProgress.Result.Statistics).RepeatPacketReceived(receivedPacket);
                Debug.WriteLine("RECV PKT previous");
                if (MessageExchangeParameters.AllowAutoLevelAdjustment)
                    await RunInUiContext(async () => await RileyLink.TxLevelUp(messageProgress));
                return null;
            }

            this.LastReceivedPacket = receivedPacket;
            this.Pod.RuntimeVariables.PacketSequence = (receivedPacket.Sequence + 1) % 32;

            if (receivedPacket.Type != expected_type)
            {
                ((RileyLinkStatistics)messageProgress.Result.Statistics).UnexpectedPacketReceived(receivedPacket);
                Debug.WriteLine("RECV PKT unexpected type");
                throw new OmniCoreErosException(FailureType.PodResponseUnexpected, "Unexpected packet type received", receivedPacket, expected_type);
            }

            if (receivedPacket.Sequence != (packet_to_send.Sequence + 1) % 32)
            {
                this.Pod.RuntimeVariables.PacketSequence = (receivedPacket.Sequence + 1) % 32;
                Debug.WriteLine("RECV PKT unexpected sequence");
                this.LastReceivedPacket = receivedPacket;
                ((RileyLinkStatistics)messageProgress.Result.Statistics).UnexpectedPacketReceived(receivedPacket);
                throw new OmniCoreErosException(FailureType.PodResponseUnexpected, "Incorrect packet sequence received", receivedPacket);
            }

            ((RileyLinkStatistics)messageProgress.Result.Statistics).PacketReceived(receivedPacket);
            return receivedPacket;
        }

        private async Task SendPacket(IMessageExchangeProgress messageProgress, 
            RadioPacket packet_to_send, int timeout = 25000)
        {
            int start_time = 0;
            Bytes received = null;
            while (start_time == 0 || Environment.TickCount - start_time < timeout)
            {
                try
                {
                    Debug.WriteLine($"SEND PKT {packet_to_send}");

                    try
                    {
                        received = await RunInUiContext(async () => await RileyLink.SendAndGetPacket(messageProgress, packet_to_send.GetPacketData(), 0, 0, 300, 3, 300));
                    }
                    catch(OmniCoreTimeoutException)
                    {
                        Debug.WriteLine("Silence fell.");
                        this.Pod.RuntimeVariables.PacketSequence++;
                        return;
                    }

                    if (start_time == 0)
                        start_time = Environment.TickCount;

                    var p = this.GetPacket(received);
                    if (p == null)
                    {
                        if (MessageExchangeParameters.AllowAutoLevelAdjustment)
                            await RunInUiContext(async () => await RileyLink.TxLevelDown(messageProgress));
                        continue;
                    }

                    if (p.Address != this.Pod.RadioAddress && p.Address != 0xFFFFFFFF)
                    {
                        Debug.WriteLine("RECV PKT ADDR MISMATCH");
                        if (MessageExchangeParameters.AllowAutoLevelAdjustment)
                            await RunInUiContext(async () => await RileyLink.TxLevelDown(messageProgress));
                        continue;
                    }

                    this.LastPacketSent = Environment.TickCount;
                    if (this.LastReceivedPacket != null && p.Type == this.LastReceivedPacket.Type
                        && p.Sequence == this.LastReceivedPacket.Sequence)
                    {
                        Debug.WriteLine("RECV PKT previous");
                        if (MessageExchangeParameters.AllowAutoLevelAdjustment)
                            await RunInUiContext(async () => await RileyLink.TxLevelUp(messageProgress));
                        continue;
                    }

                    Debug.WriteLine($"RECV PKT {p}");
                    Debug.WriteLine($"RECEIVED unexpected packet");
                    this.LastReceivedPacket = p;
                    this.Pod.RuntimeVariables.PacketSequence = p.Sequence + 1;
                    packet_to_send.WithSequence(this.Pod.RuntimeVariables.PacketSequence);
                    start_time = Environment.TickCount;
                    continue;
                }
                catch (OmniCoreRadioException pre)
                {
                    Debug.WriteLine($"Radio error during send, retrying {pre}");
                    await RunInUiContext(async () => await RileyLink.Reset(messageProgress));
                    start_time = Environment.TickCount;
                }
                catch (Exception) { throw; }
            }
            Debug.WriteLine("Exceeded timeout while waiting for silence to fall");
        }

        private RadioPacket GetPacket(Bytes data)
        {
            if (data != null && data.Length > 1)
            {
                byte rssi = data[0];
                try
                {
                    var rp = RadioPacket.Parse(data.Sub(2));
                    if (rp != null)
                        rp.Rssi = rssi;
                    return rp;
                }
                catch
                {
                    Debug.WriteLine($"RECV INVALID DATA {data}");
                }
            }
            return null;
        }

        private RadioPacket CreateAckPacket(uint address1, uint address2, int sequence)
        {
            return new RadioPacket(address1, PacketType.ACK, sequence, new Bytes(address2));
        }

        public List<RadioPacket> GetRadioPackets(ErosMessage message)
        {
            var message_body_len = 0;
            foreach (var p in message.GetParts())
            {
                var ep = p as ErosRequest;
                var cmd_body = ep.PartData;
                message_body_len += (int)cmd_body.Length + 2;
                if (ep.RequiresNonce)
                    message_body_len += 4;
            }

            byte b0 = 0;
            if (MessageExchangeParameters.CriticalWithFollowupRequired)
                b0 = 0x80;

            var msgSequence = Pod.LastStatus != null ? (Pod.LastStatus.MessageSequence + 1 % 16) : 0;
            if (MessageExchangeParameters.MessageSequenceOverride.HasValue)
                msgSequence = MessageExchangeParameters.MessageSequenceOverride.Value;

            b0 |= (byte)(msgSequence << 2);
            b0 |= (byte)((message_body_len >> 8) & 0x03);
            byte b1 = (byte)(message_body_len & 0xff);

            var msgAddress = Pod.RadioAddress;
            if (MessageExchangeParameters.AddressOverride.HasValue)
                msgAddress = MessageExchangeParameters.AddressOverride.Value;

            var message_body = new Bytes(msgAddress);
            message_body.Append(b0);
            message_body.Append(b1);

            foreach (var p in message.GetParts())
            {
                var ep = p as ErosRequest;
                var cmd_type = (byte) ep.PartType;
                var cmd_body = ep.PartData;

                if (ep.RequiresNonce)
                {
                    message_body.Append(cmd_type);
                    message_body.Append((byte)(cmd_body.Length + 4));

                    if (Pod.RuntimeVariables.NonceSync.HasValue)
                        MessageExchangeParameters.Nonce.Sync(msgSequence);

                    var nonce = MessageExchangeParameters.Nonce.GetNext();
                    message_body.Append(nonce);
                }
                else
                {
                    if (cmd_type == (byte)PartType.ResponseStatus)
                        message_body.Append(cmd_type);
                    else
                    {
                        message_body.Append(cmd_type);
                        message_body.Append((byte)cmd_body.Length);
                    }
                }
                message_body.Append(cmd_body);
            }
            var crc_calculated = CrcUtil.Crc16(message_body.ToArray());
            message_body.Append(crc_calculated);

            int index = 0;
            bool first_packet = true;
            int sequence = Pod.RuntimeVariables.PacketSequence;
            var radio_packets = new List<RadioPacket>();

            while (index < message_body.Length)
            {
                var to_write = Math.Min(31, message_body.Length - index);
                var packet_body = message_body.Sub(index, index + to_write);
                radio_packets.Add(new RadioPacket(msgAddress,
                                                first_packet ? PacketType.PDM : PacketType.CON,
                                                sequence,
                                                packet_body));
                first_packet = false;
                sequence = (sequence + 2) % 32;
                index += to_write;
            }

            if (MessageExchangeParameters.RepeatFirstPacket)
            {
                var fp = radio_packets[0];
                radio_packets.Insert(0, fp);
            }
            return radio_packets;
        }

        public void ParseResponse(IMessage response, IPod pod, IMessageExchangeProgress progress)
        {
            foreach (var mp in response.GetParts())
            {
                var er = mp as ErosResponse;
                er.Parse(pod, progress.Result);
            }
        }
    }
}