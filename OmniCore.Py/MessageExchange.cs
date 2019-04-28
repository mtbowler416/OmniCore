﻿using Omni.Py;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OmniCore.Py
{
    public class MessageExchange
    {
        public int unique_packets = 0;
        public int repeated_sends = 0;
        public int receive_timeouts = 0;
        public int repeated_receives = 0;
        public int protocol_errors = 0;
        public int bad_packets = 0;
        public int radio_errors = 0;
        public bool successful = false;
        public DateTime Started;
        public DateTime Ended;

        public PdmMessage PdmMessage;
        public PodMessage PodMessage;
        public Exception Error;

        private IPacketRadio PacketRadio;
        private Pod Pod;

        private logger Logger;
        private logger PacketLogger;

        public RadioPacket last_received_packet;
        public int last_packet_timestamp = 0;

        public MessageExchange(PdmMessage pdmMessage, IPacketRadio packetRadio, Pod pod)
        {
            this.PdmMessage = pdmMessage;
            this.PacketRadio = packetRadio;
            this.Pod = pod;
            this.Logger = definitions.getLogger();
            this.PacketLogger = definitions.get_packet_logger();
        }

        private void reset_sequences()
        {
            this.Pod.radio_packet_sequence = 0;
            this.Pod.radio_message_sequence = 0;
        }

        public async Task<PodMessage> GetPodResponse()
        {
            this.Started = DateTime.UtcNow;
            if (this.PdmMessage.TxLevel.HasValue)
            {
                this.PacketRadio.set_tx_power(this.PdmMessage.TxLevel.Value);
            }

            if (!this.PdmMessage.address.HasValue)
                this.PdmMessage.address = this.Pod.radio_address;

            if (!this.PdmMessage.sequence.HasValue)
                this.PdmMessage.sequence = this.Pod.radio_message_sequence;


            var packets = this.PdmMessage.get_radio_packets(this.Pod.radio_packet_sequence);

            RadioPacket received = null;
            var packet_count = packets.Count;

            this.unique_packets = packet_count * 2;

            for (int part = 0; part < packet_count; part++)
            {
                var packet = packets[part];
                int repeat_count = -1;
                int timeout = 10000;
                while (true)
                {
                    repeat_count++;
                    if (repeat_count == 0)
                        this.Logger.log($"Sending PDM message part {part + 1}/{packet_count}");
                    else
                        this.Logger.log($"Sending PDM message part {part + 1}/{packet_count} (Repeat: {repeat_count})");

                    RadioPacketType expected_type;
                    if (part == packet_count - 1)
                        expected_type = RadioPacketType.POD;
                    else
                        expected_type = RadioPacketType.ACK;

                    try
                    {
                        received = await this.ExchangePackets(packet.with_sequence(this.Pod.radio_packet_sequence), expected_type, timeout);
                        break;
                    }
                    catch (OmnipyTimeoutError)
                    {
                        this.Logger.log("Trying to recover from timeout error");
                        if (part == 0)
                        {
                            if (repeat_count == 0)
                            {
                                timeout = 15000;
                                continue;
                            }
                            else if (repeat_count == 1)
                            {
                                this.reset_sequences();
                                timeout = 10000;
                                Thread.Sleep(2000);
                                continue;
                            }
                            else if (repeat_count == 2)
                            {
                                this.reset_sequences();
                                timeout = 15000;
                                continue;
                            }
                            else
                            {
                                this.Logger.log("Failed recovery");
                                if (packet_count == 1)
                                {
                                    this.Logger.log("Calming pod down in case it's still broadcasting");
                                    var ack_packet = this.final_ack(this.PdmMessage.AckAddressOverride.Value, 2);
                                    try
                                    {
                                        this.PacketRadio.set_tx_power(TxPower.Highest);
                                        await this.SendPacket(ack_packet);
                                    }
                                    catch (Exception e)
                                    {
                                        this.Logger.exception("Ignored.", e);
                                    }
                                }
                                this.reset_sequences();
                                throw;
                            }
                        }
                        else if (part < packet_count - 1)
                        {
                            if (repeat_count < 2)
                            {
                                timeout = 20000;
                                continue;
                            }
                            else
                                throw;
                        }
                        else
                        {
                            if (repeat_count < 10)
                            {
                                timeout = 20000;
                                continue;
                            }
                            else
                                throw;
                        }
                    }
                    catch (PacketRadioError)
                    {
                        this.Logger.log("Trying to recover from radio error");
                        this.radio_errors++;
                        if (part == 0)
                        {
                            if (repeat_count < 3)
                            {
                                this.PacketRadio.reset();
                                timeout = 10000;
                                Thread.Sleep(2000);
                                continue;
                            }
                            else
                            {
                                this.Logger.log("Failed recovery");
                                this.reset_sequences();
                                throw;
                            }
                        }
                        else if (part < packet_count - 1)
                        {
                            if (repeat_count < 6)
                            {
                                this.PacketRadio.reset();
                                timeout = 10000;
                                Thread.Sleep(2000);
                                continue;
                            }
                            else
                            {
                                this.Logger.log("Failed recovery");
                                this.reset_sequences();
                                throw;
                            }
                        }
                        else
                        {
                            if (repeat_count < 10)
                            {
                                this.PacketRadio.reset();
                                timeout = 10000;
                                Thread.Sleep(2000);
                                continue;
                            }
                            else
                            {
                                this.Logger.log("Failed recovery");
                                this.reset_sequences();
                                throw;
                            }
                        }
                    }
                    catch (ProtocolError pe)
                    {
                        if (pe.ReceivedPacket != null && expected_type == RadioPacketType.POD && pe.ReceivedPacket.type == RadioPacketType.ACK)
                        {
                            this.Logger.log("Trying to recover from protocol error");
                            this.Pod.radio_packet_sequence = (pe.ReceivedPacket.sequence + 1) % 32;
                            packet = this.interim_ack(this.PdmMessage.AckAddressOverride.Value, this.Pod.radio_packet_sequence);
                            continue;
                        }
                        else
                            throw;
                    }
                }
                part++;
                this.Pod.radio_packet_sequence = (received.sequence + 1) % 32;
            }

            this.PacketLogger.log($"SENT MSG {this.PdmMessage}");

            var part_count = 0;
            if (received.type == RadioPacketType.POD)
            {
                part_count = 1;
                this.Logger.log($"Received POD message part {part_count}");
            }
            var pod_response = new PodMessage();
            while (!pod_response.add_radio_packet(received))
            {
                var ack_packet = this.interim_ack(this.PdmMessage.AckAddressOverride.Value, (received.sequence + 1) % 32);
                received = await this.ExchangePackets(ack_packet, RadioPacketType.CON);
                part_count++;
                this.Logger.log($"Received POD message part {part_count}");
            }

            this.PacketLogger.log($"RCVD MSG {pod_response}");
            this.Logger.log("Send and receive completed.");
            this.Pod.radio_message_sequence = (pod_response.sequence.Value + 1) % 16;
            this.Pod.radio_packet_sequence = (received.sequence + 1) % 32;

            return pod_response;

        }


        private async Task<RadioPacket> ExchangePackets(RadioPacket packet_to_send, RadioPacketType expected_type, int timeout = 10000)
        {
            int start_time = 0;
            bool first = true;
            byte[] received = null;
            while (start_time == 0 || Environment.TickCount - start_time < timeout)
            {
                if (first)
                    first = false;
                else
                    this.repeated_sends += 1;

                if (this.last_packet_timestamp == 0 || (Environment.TickCount - this.last_packet_timestamp) > 4000)
                    received = await this.PacketRadio.send_and_receive_packet(packet_to_send.get_data(), 0, 0, 300, 1, 300);
                else
                    received = await this.PacketRadio.send_and_receive_packet(packet_to_send.get_data(), 0, 0, 120, 0, 40);
                if (start_time == 0)
                    start_time = Environment.TickCount;

                this.PacketLogger.log($"SEND PKT {packet_to_send}");

                if (received == null)
                {
                    this.receive_timeouts++;
                    this.PacketLogger.log("RECV PKT None");
                    this.PacketRadio.tx_up();
                    continue;
                }

                var p = await this.GetPacket(received);
                if (p == null)
                {
                    this.bad_packets++;
                    this.PacketRadio.tx_down();
                    continue;
                }

                this.PacketLogger.log($"RECV PKT {p}");
                if (p.address != this.Pod.radio_address)
                {
                    this.bad_packets++;
                    this.PacketLogger.log("RECV PKT ADDR MISMATCH");
                    this.PacketRadio.tx_down();
                    continue;
                }

                this.last_packet_timestamp = Environment.TickCount;

                if (this.last_received_packet != null && p.sequence == this.last_received_packet.sequence
                    && p.type == this.last_received_packet.type)
                {
                    this.repeated_receives++;
                    this.PacketLogger.log("RECV PKT previous");
                    this.PacketRadio.tx_up();
                    continue;
                }

                this.last_received_packet = p;
                this.Pod.radio_packet_sequence = (p.sequence + 1) % 32;

                if (p.type != expected_type)
                {
                    this.PacketLogger.log("RECV PKT unexpected type");
                    this.protocol_errors++;
                    throw new ProtocolError("Unexpected packet type received");
                }

                if (p.sequence != (packet_to_send.sequence + 1) % 32)
                {
                    this.Pod.radio_packet_sequence = (p.sequence + 1) % 32;
                    this.PacketLogger.log("RECV PKT unexpected sequence");
                    this.last_received_packet = p;
                    this.protocol_errors++;
                    throw new ProtocolError("Incorrect packet sequence received");
                }

                return p;

            }
            throw new OmnipyTimeoutError("Exceeded timeout while send and receive");
        }

        private async Task SendPacket(RadioPacket packet_to_send, int allow_premature_exit_after = -1, int timeout = 25000)
        {
            int start_time = 0;
            this.unique_packets++;
            byte[] received = null;
            while (start_time == 0 || Environment.TickCount - start_time < timeout)
            {
                try
                {
                    this.PacketLogger.log($"SEND PKT {packet_to_send}");

                    received = await this.PacketRadio.send_and_receive_packet(packet_to_send.get_data(), 0, 0, 300, 0, 40);

                    if (start_time == 0)
                        start_time = Environment.TickCount;

                    //if (allow_premature_exit_after >= 0 && Environment.TickCount - start_time >= allow_premature_exit_after)
                    //{
                    //    if (this.request_arrived.WaitOne(0))
                    //    {
                    //        this.logger.log("Prematurely exiting final phase to process next request");
                    //        this.packet_sequence = (this.packet_sequence + 1) % 32;
                    //        break;
                    //    }
                    //}

                    if (received == null)
                    {
                        received = await this.PacketRadio.get_packet(600);
                        if (received == null)
                        {
                            this.PacketLogger.log("Silence fell.");
                            this.Pod.radio_packet_sequence = (this.Pod.radio_packet_sequence + 1) % 32;
                            break;
                        }
                    }

                    var p = await this.GetPacket(received);
                    if (p == null)
                    {
                        this.bad_packets++;
                        this.PacketRadio.tx_down();
                        continue;
                    }

                    if (p.address != this.Pod.radio_address)
                    {
                        this.bad_packets++;
                        this.PacketLogger.log("RECV PKT ADDR MISMATCH");
                        this.PacketRadio.tx_down();
                        continue;
                    }

                    this.last_packet_timestamp = Environment.TickCount;
                    if (this.last_received_packet != null && p.type == this.last_received_packet.type
                        && p.sequence == this.last_received_packet.sequence)
                    {
                        this.repeated_receives++;
                        this.PacketLogger.log("RECV PKT previous");
                        this.PacketRadio.tx_up();
                        continue;
                    }

                    this.PacketLogger.log($"RECV PKT {p}");
                    this.PacketLogger.log($"RECEIVED unexpected packet");
                    this.protocol_errors++;
                    this.last_received_packet = p;
                    this.Pod.radio_packet_sequence = (p.sequence + 1) % 32;
                    packet_to_send.with_sequence(this.Pod.radio_packet_sequence);
                    start_time = Environment.TickCount;
                    continue;
                }
                catch (PacketRadioError pre)
                {
                    this.radio_errors++;
                    this.Logger.exception("Radio error during send, retrying", pre);
                    this.PacketRadio.reset();
                    start_time = Environment.TickCount;
                }
            }
            this.Logger.log("Exceeded timeout while waiting for silence to fall");
        }

        private async Task<RadioPacket> GetPacket(byte[] data)
        {
            if (data != null && data.Length > 2)
            {
                byte rssi = data[0];
                try
                {
                    var rp = RadioPacket.parse(data.Sub(2));
                    rp.rssi = rssi;
                    return rp;
                }
                catch
                {
                    this.PacketLogger.log($"RECV INVALID DATA {data.Sub(2)}");
                }
            }
            return null;
        }

        private RadioPacket _ack_data(uint address1, uint address2, int sequence)
        {
            return new RadioPacket(address1, RadioPacketType.ACK, sequence, address2.ToBytes());
        }

        private RadioPacket interim_ack(uint ack_address_override, int sequence)
        {
            if (ack_address_override == this.Pod.radio_address)
                return _ack_data(this.Pod.radio_address, this.Pod.radio_address, sequence);
            else
                return _ack_data(this.Pod.radio_address, ack_address_override, sequence);
        }

        private RadioPacket final_ack(uint ack_address_override, int sequence)
        {
            if (ack_address_override == this.Pod.radio_address)
                return _ack_data(this.Pod.radio_address, 0, sequence);
            else
                return _ack_data(this.Pod.radio_address, ack_address_override, sequence);
        }

    }
}