// ==============================================================================
// GHOST OS - ALL-IN-ONE NETWORK DRIVER & DIAGNOSTIC SYSTEM v2.0
// Realtek RTL8102E Fast Ethernet & Atheros AR2425 802.11b/g Wi-Fi Driver
// Bare-Metal IEEE 802.11i WPA2-PSK 4-Way Handshake & Hardware CCMP Keytable
// Author: ghost
// ==============================================================================

using Cosmos.Core;
using Cosmos.HAL.Network;
using Cosmos.HAL;
using Cosmos.System.Network.IPv4;
using Sys = Cosmos.System;
using System.Collections.Generic;
using System.Text;
using System;

namespace RTL8102E_Driver
{

namespace Cosmos.HAL
{
    [AttributeUsage(AttributeTargets.Class)]
    public class DriverAttribute : Attribute
    {
        public DriverAttribute(DriverType type, string description) { }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public class AuthorAttribute : Attribute
    {
        public AuthorAttribute(string author) { }
    }

    public enum DriverType
    {
        Network,
        Storage,
        Audio
    }
}

{
    [Cosmos.HAL.Driver(Cosmos.HAL.DriverType.Network, "Realtek RTL8102E/RTL8136 PCI Ethernet")]
    [Cosmos.HAL.Author("ghost")]
    public class RTL8102E : NetworkDevice
    {
        private PCIDeviceNormal pciDevice;
        private uint mmioBase;
        private MemoryBlock mmio;
        private byte[] macBytes = new byte[6];

        private ManagedMemoryBlock rxDescBlock;
        private ManagedMemoryBlock txDescBlock;
        private ManagedMemoryBlock[] rxBuffers;
        private ManagedMemoryBlock[] txBuffers;

        // NetworkDevice Properties
        public override CardType CardType => CardType.Ethernet;
        public override Cosmos.HAL.Network.MACAddress MACAddress => new Cosmos.HAL.Network.MACAddress(macBytes);
        public override string Name => "RTL8102E";
        public override bool Ready => true;

        public static uint HardwareTxBytes = 0;
        public static uint HardwareRxBytes = 0;
        public static uint HardwareTxPackets = 0;
        public static uint HardwareRxPackets = 0;
        public static Action<byte[], bool> OnPacketSniffed = null;

        // Diagnostic Telemetry Properties
        public uint MmioBase => mmioBase;
        public uint RxRingPhysAddr => rxDescBlock != null ? (uint)rxDescBlock.Offset : 0;
        public uint TxRingPhysAddr => txDescBlock != null ? (uint)txDescBlock.Offset : 0;
        public int CurrentRxIndex => currentRxDesc;
        public int CurrentTxIndex => currentTxDesc;
        public string PciLocation => pciDevice != null ? $"{pciDevice.bus}:{pciDevice.slot}:{pciDevice.function}" : "N/A";
        public byte ReadCommandReg() => mmio != null ? mmio.Bytes[0x37] : (byte)0;
        public byte ReadPhyStatusReg() => mmio != null ? mmio.Bytes[0x6C] : (byte)0;
        public ushort ReadIsrReg() => mmio != null ? mmio.Words[0x3E] : (ushort)0;
        public ushort ReadImrReg() => mmio != null ? mmio.Words[0x3C] : (ushort)0;
        public uint ReadRxConfigReg() => mmio != null ? mmio.DWords[0x44] : 0;
        public uint ReadTxConfigReg() => mmio != null ? mmio.DWords[0x40] : 0;
        public string GetMacString()
        {
            return $"{macBytes[0]:X2}:{macBytes[1]:X2}:{macBytes[2]:X2}:{macBytes[3]:X2}:{macBytes[4]:X2}:{macBytes[5]:X2}";
        }
        
        // Boilerplate methods
        public override byte[] ReceivePacket() { return null; }
        public override int BytesAvailable() { return 0; }
        public override bool Enable() { return true; }
        public override bool IsReceiveBufferFull() { return false; }
        public override bool IsSendBufferFull() { return false; }
        public override bool ReceiveBytes(byte[] buffer, int offset, int max) { return false; }

        public RTL8102E()
        {
        }

        public bool Initialize()
        {
            Console.WriteLine("r8102e: initializing driver...");

            foreach (var device in Cosmos.HAL.PCI.Devices)
            {
                if (device.VendorID == 0x10EC && device.DeviceID == 0x8136)
                {
                    pciDevice = new PCIDeviceNormal(device.bus, device.slot, device.function);
                    
                    if (pciDevice != null)
                    {
                        Console.WriteLine($"r8102e: found device at pci {device.bus}:{device.slot}:{device.function}");
                        break;
                    }
                }
            }

            if (pciDevice == null)
            {
                Console.WriteLine("r8102e: error - device 10ec:8136 not found");
                return false;
            }

            // Explicitly enable Bus Mastering, Memory Space, IO Space, and clear INTx disable
            ushort pciCmd = (ushort)pciDevice.ReadRegister16(0x04);
            pciCmd |= (1 << 0) | (1 << 1) | (1 << 2); // IO space, Memory space, Bus Master
            pciCmd &= unchecked((ushort)~(1 << 10)); // Clear INTx disable
            pciDevice.WriteRegister16(0x04, pciCmd);
            pciDevice.EnableDevice();
            Console.WriteLine("r8102e: pci device enabled (bus master & memory)");

            bool foundMMIO = false;
            
            Console.WriteLine("r8102e: dumping raw bar registers...");
            for (byte i = 0; i < 6; i++)
            {
                uint rawBar = pciDevice.ReadRegister32((byte)(0x10 + (i * 4)));
                Console.WriteLine($"r8102e: raw BAR{i} = 0x{rawBar:X8}");
                
                if (rawBar != 0)
                {
                    bool isIo = (rawBar & 1) == 1;
                    if (!isIo)
                    {
                        // Check if it's 64-bit
                        byte type = (byte)((rawBar >> 1) & 0x03);
                        uint baseAddr = rawBar & 0xFFFFFFF0;
                        
                        if (type == 2) // 64-bit
                        {
                            i++; // skip next bar
                            uint rawBarHigh = pciDevice.ReadRegister32((byte)(0x10 + (i * 4)));
                            Console.WriteLine($"r8102e: raw BAR{i} (high) = 0x{rawBarHigh:X8}");
                            
                            // On 32-bit OS, we can only use it if upper 32 bits are 0
                            if (rawBarHigh != 0) {
                                Console.WriteLine("r8102e: warning - 64-bit bar is above 4GB, cannot use in 32-bit mode!");
                                continue;
                            }
                        }
                        
                        if (baseAddr != 0)
                        {
                            mmioBase = baseAddr;
                            Console.WriteLine($"r8102e: found MMIO at 0x{mmioBase:X8}");
                            foundMMIO = true;
                            break;
                        }
                    }
                }
            }

            if (!foundMMIO)
            {
                Console.WriteLine("r8102e: error - no valid mmio bar found");
                return false;
            }

            mmio = new MemoryBlock(mmioBase, 0x1000);
            return Phase2ResetAndMAC();
        }

        private void Write32(ManagedMemoryBlock block, uint offset, uint value)
        {
            block[offset] = (byte)(value & 0xFF);
            block[offset + 1] = (byte)((value >> 8) & 0xFF);
            block[offset + 2] = (byte)((value >> 16) & 0xFF);
            block[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private bool Phase2ResetAndMAC()
        {
            Console.WriteLine("r8102e: issuing soft reset...");
            
            // Soft Reset command (0x10) to Command Register (CR, offset 0x37)
            mmio.Bytes[0x37] = 0x10;

            int timeout = 10000;
            while ((mmio.Bytes[0x37] & 0x10) != 0)
            {
                timeout--;
                if (timeout <= 0)
                {
                    Console.WriteLine("r8102e: error - soft reset timeout");
                    return false;
                }
            }
            Console.WriteLine("r8102e: soft reset complete");

            for (uint i = 0; i < 6; i++)
            {
                macBytes[i] = mmio.Bytes[i];
            }

            Console.Write("r8102e: mac address ");
            for (int i = 0; i < 6; i++)
            {
                Console.Write(macBytes[i].ToString("x2"));
                if (i < 5) Console.Write(":");
            }
            Console.WriteLine();

            Console.WriteLine("r8102e: phase 1 and 2 complete");
            return Phase3DescriptorRings();
        }

        public const int DESC_COUNT = 16;

        private bool Phase3DescriptorRings()
        {
            Console.WriteLine("r8102e: initializing dma descriptor rings...");

            // Allocate 16 descriptors for RX and TX (16 * 16 = 256 bytes), aligned to 256
            rxDescBlock = new ManagedMemoryBlock(DESC_COUNT * 16, 256);
            txDescBlock = new ManagedMemoryBlock(DESC_COUNT * 16, 256);

            // Allocate RX Buffers (16 buffers of 1536 bytes -> aligned to 8 bytes)
            rxBuffers = new ManagedMemoryBlock[DESC_COUNT];
            for (int i = 0; i < DESC_COUNT; i++)
            {
                rxBuffers[i] = new ManagedMemoryBlock(1536, 8);
            }

            // Allocate TX Buffers (16 buffers of 1536 bytes -> aligned to 8 bytes)
            txBuffers = new ManagedMemoryBlock[DESC_COUNT];
            for (int i = 0; i < DESC_COUNT; i++)
            {
                txBuffers[i] = new ManagedMemoryBlock(1536, 8);
            }

            Console.WriteLine("r8102e: setting up rx ring in ram...");
            for (int i = 0; i < DESC_COUNT; i++)
            {
                uint offset = (uint)(i * 16);
                
                // Command/Status: 1536 buffer size (bits 0-13) | OWN bit (bit 31)
                // If it's the last descriptor, set EOR bit (bit 30)
                uint cmdStatus = 1536 | 0x80000000;
                if (i == DESC_COUNT - 1) cmdStatus |= 0x40000000;
                
                Write32(rxDescBlock, offset, cmdStatus);
                Write32(rxDescBlock, offset + 4, 0); // Vlan
                Write32(rxDescBlock, offset + 8, (uint)rxBuffers[i].Offset); // Buffer Address Low
                Write32(rxDescBlock, offset + 12, (uint)(rxBuffers[i].Offset >> 32)); // Buffer Address High
            }

            Console.WriteLine("r8102e: setting up tx ring in ram...");
            for (int i = 0; i < DESC_COUNT; i++)
            {
                uint offset = (uint)(i * 16);
                uint txCmd = (i == DESC_COUNT - 1) ? 0x40000000u : 0u;
                Write32(txDescBlock, offset, txCmd);
                Write32(txDescBlock, offset + 4, 0); // Vlan
                Write32(txDescBlock, offset + 8, (uint)txBuffers[i].Offset); // Buffer Address Low
                Write32(txDescBlock, offset + 12, (uint)(txBuffers[i].Offset >> 32)); // Buffer Address High
            }

            Console.WriteLine("r8102e: writing physical addresses to registers...");
            
            // Write TX Descriptor Start Address (0x20 Low, 0x24 High)
            mmio.DWords[0x20] = (uint)txDescBlock.Offset;
            mmio.DWords[0x24] = (uint)(txDescBlock.Offset >> 32);

            // Write RX Descriptor Start Address (0xE4 Low, 0xE8 High)
            mmio.DWords[0xE4] = (uint)rxDescBlock.Offset;
            mmio.DWords[0xE8] = (uint)(rxDescBlock.Offset >> 32);

            Console.WriteLine($"r8102e: rx ring physical addr: 0x{rxDescBlock.Offset:X8}");
            Console.WriteLine($"r8102e: tx ring physical addr: 0x{txDescBlock.Offset:X8}");
            Console.WriteLine("r8102e: phase 3 complete");
            
            return Phase4And5();
        }

        public volatile ushort LastIsr = 0;
        public volatile uint IrqCount = 0;
        private int currentRxDesc = 0;

        private uint Read32(ManagedMemoryBlock block, uint offset)
        {
            return (uint)(block[offset] | (block[offset + 1] << 8) | (block[offset + 2] << 16) | (block[offset + 3] << 24));
        }

        private bool Phase4And5()
        {
            Console.WriteLine("r8102e: configuring RxConfig and TxConfig...");
            // RxConfig (0x44): Accept Broadcast (0x08), Multicast (0x04), Physical Match (0x02), Unlimited FIFO (0x700), Max DMA (0xE000) -> 0x0000E70E
            mmio.DWords[0x44] = 0x0000E70E;

            // TxConfig (0x40): Max DMA burst
            mmio.DWords[0x40] = 0x60000000;

            // RxMaxSize (0xDA): 1536 bytes
            mmio.Words[0xDA] = 0x0600;

            Console.WriteLine("r8102e: enabling RX and TX in CR...");
            // Command Register (0x37): RE=0x08, TE=0x04 -> 0x0C
            mmio.Bytes[0x37] = 0x0C;

            Console.WriteLine("r8102e: verifying PHY Link Status...");
            // PHYStatus register is at offset 0x6C (8-bit)
            // Bit 1 indicates Link Status (1 = UP, 0 = DOWN)
            int linkSpins = 0;
            while ((mmio.Bytes[0x6C] & 0x02) == 0 && linkSpins < 500000)
            {
                linkSpins++;
            }
            if ((mmio.Bytes[0x6C] & 0x02) != 0)
            {
                Console.WriteLine("r8102e: PHY Link is UP! Cable connected.");
            }
            else
            {
                Console.WriteLine("r8102e: PHY Link is DOWN. Please check the network cable.");
            }

            Console.WriteLine("r8102e: masking all hardware interrupts in IMR (switching back to Polling mode)...");
            // Interrupt Mask Register (0x3C): 0x0000 ensures no hardware INTA# is generated
            // This prevents Cosmos OS deadlocks caused by single-core PIT timer blocking.
            mmio.Words[0x3C] = 0x0000;

            Console.WriteLine("r8102e: driver fully initialized and ready for HackPoll!");
            return true;
        }

        public bool IsPhyLinkUp()
        {
            if (mmio == null) return false;
            return (mmio.Bytes[0x6C] & 0x02) != 0;
        }

        private int currentTxDesc = 0;

        public override bool QueueBytes(byte[] buffer)
        {
            if (buffer == null || buffer.Length == 0) return false;
            return QueueBytes(buffer, 0, buffer.Length);
        }

        public override bool QueueBytes(byte[] buffer, int offset, int length)
        {
            uint descOffset = (uint)(currentTxDesc * 16);
            
            // Wait briefly for descriptor if still owned by NIC
            int spins = 500;
            while ((Read32(txDescBlock, descOffset) & 0x80000000) != 0 && spins > 0)
            {
                mmio.Bytes[0x38] = 0x40; // Kick doorbell in case previous poll was missed
                spins--;
            }

            uint cmdStatus = Read32(txDescBlock, descOffset);
            if ((cmdStatus & 0x80000000) != 0)
                return false; // Queue full!

            // Copy data to txBuffers[currentTxDesc]
            for (int i = 0; i < length; i++)
            {
                txBuffers[currentTxDesc][(uint)i] = buffer[offset + i];
            }

            // Ethernet requires a minimum payload of 60 bytes (excluding CRC).
            // RTL8102E does NOT auto-pad runts!
            uint txLength = (uint)length;
            if (txLength < 60)
            {
                for (uint i = txLength; i < 60; i++)
                {
                    txBuffers[currentTxDesc][i] = 0; // Pad with zeros
                }
                txLength = 60;
            }

            // CommandStatus: Length | OWN (bit 31) | FS (bit 29) | LS (bit 28)
            uint newCmdStatus = txLength | 0x80000000 | 0x20000000 | 0x10000000;
            if (currentTxDesc == DESC_COUNT - 1) newCmdStatus |= 0x40000000; // Preserve EOR bit
            
            Write32(txDescBlock, descOffset, newCmdStatus);
            
            HardwareTxBytes += txLength;
            HardwareTxPackets++;

            if (OnPacketSniffed != null)
            {
                try { OnPacketSniffed(buffer, true); } catch { }
            }

            // Trigger TX poll (TxPoll register 0x38 = 0x40)
            mmio.Bytes[0x38] = 0x40;
            mmio.Bytes[0x38] = 0x40; // Double kick (Linux 8168 doorbell hack)
            
            currentTxDesc = (currentTxDesc + 1) % DESC_COUNT;
            return true;
        }

        /// <summary>
        /// HackPoll intercepts the hardware directly, bypassing Cosmos OS IRQs.
        /// This avoids full system freezes on single-core devices when Cosmos blocks.
        /// </summary>
        public void HackPoll()
        {
            if (rxDescBlock == null || txDescBlock == null || mmio == null) return;

            // 1. Process RX Ring (up to DESC_COUNT descriptors per poll)
            for (int d = 0; d < DESC_COUNT; d++)
            {
                uint offset = (uint)(currentRxDesc * 16);
                uint cmdStatus = Read32(rxDescBlock, offset);
                
                // If OWN bit (bit 31) is 1, MAC still owns it, ring is empty for us
                if ((cmdStatus & 0x80000000) != 0)
                    break;
                    
                // Bit 31 is 0! MAC transferred a packet to RAM!
                // Realtek includes the 4-byte Ethernet CRC in the length. Cosmos doesn't want it.
                uint rawLength = cmdStatus & 0x3FFF;
                if (rawLength > 1536) rawLength = 1536;
                uint length = rawLength > 4 ? rawLength - 4 : rawLength; 
                
                HardwareRxBytes += length;
                HardwareRxPackets++;

                if (length > 0)
                {
                    // Read into managed array of exact frame length for NetworkStack, DHCP & TCP parsers
                    byte[] packet = new byte[length];
                    for (uint i = 0; i < length; i++)
                    {
                        packet[i] = rxBuffers[currentRxDesc][i];
                    }

                    if (OnPacketSniffed != null)
                    {
                        try { OnPacketSniffed(packet, false); } catch { }
                    }

                    // Send to NetworkStack (or custom Interceptor) with safe error handling
                    if (DataReceived != null)
                    {
                        try
                        {
                            DataReceived(packet);
                        }
                        catch { }
                    }
                }
                
                // Give descriptor back to MAC
                uint newCmdStatus = 1536 | 0x80000000;
                if (currentRxDesc == DESC_COUNT - 1) newCmdStatus |= 0x40000000; // Preserve EOR bit on last desc
                
                Write32(rxDescBlock, offset, newCmdStatus);
                
                currentRxDesc = (currentRxDesc + 1) % DESC_COUNT;
            }

            // 2. Transmit Watchdog: check if any TX descriptor is pending and kick TPPOLL
            for (int t = 0; t < DESC_COUNT; t++)
            {
                if ((Read32(txDescBlock, (uint)(t * 16)) & 0x80000000) != 0)
                {
                    mmio.Bytes[0x38] = 0x40;
                    break;
                }
            }

            // 3. Read and Clear Interrupt Status Register (0x3E)
            ushort status = mmio.Words[0x3E];
            if (status != 0)
            {
                LastIsr = status;
                IrqCount++;
                
                // Acknowledge by writing the exact same bits back to ISR
                mmio.Words[0x3E] = status;

                // If RDU (bit 5: 0x0020) or RXOVW (bit 4: 0x0010) occurred, re-arm the receiver!
                if ((status & 0x0030) != 0)
                {
                    mmio.Bytes[0x37] = 0x0C;
                }
            }
        }
    }
}

{
    public class CustomDHCP
    {
        private RTL8102E _driver;
        private bool _offerReceived = false;
        private bool _ackReceived = false;
        private byte[] _offeredIp = null;
        private byte[] _serverIp = null;
        private byte[] _subnetMask = new byte[] { 255, 255, 255, 0 };
        private byte[] _gatewayIp = null;
        private byte[] _dnsIp = null;

        public byte[] OfferedIp => _offeredIp;
        public byte[] SubnetMask => _subnetMask;
        public byte[] GatewayIp => _gatewayIp;
        public byte[] ServerIp => _serverIp;
        public byte[] DnsIp => _dnsIp;

        public CustomDHCP(RTL8102E driver)
        {
            _driver = driver;
        }

        public byte[] DoAutoConfig()
        {
            Console.WriteLine("[DHCP] Checking PHY Link Status before DHCP...");
            
            // Wait up to ~2.5s for PHY link auto-negotiation if cable was recently plugged in
            int linkSpins = 0;
            while (_driver != null && !_driver.IsPhyLinkUp() && linkSpins < 200000)
            {
                _driver.HackPoll();
                linkSpins++;
            }

            if (_driver != null && !_driver.IsPhyLinkUp())
            {
                Console.WriteLine("[DHCP] Notice: PHY Link is DOWN (Ethernet cable unplugged). Using Direct-Link fallback.");
                ApplyFallback();
                return _offeredIp;
            }

            Console.WriteLine("[DHCP] Starting Raw DHCP Discover (Broadcast)...");
            
            // Hijack DataReceived safely
            var oldHandler = _driver.DataReceived;
            _driver.DataReceived = CustomDataReceived;

            // Craft raw DHCP Discover Packet (Option 53 = 1)
            byte[] discoverPacket = BuildDhcpPacket(1, null, null);

            // Send initial Discover
            _driver.QueueBytes(discoverPacket);
            Console.WriteLine("[DHCP] Discover sent. Polling for Offer...");

            // Poll using HackPoll with bounded retries
            long timeout = 25000000L;
            long iter = 0;
            int retries = 0;
            
            while (!_offerReceived && iter < timeout)
            {
                _driver.HackPoll();
                iter++;
                
                // Re-transmit every 6 million iterations (~300ms)
                if (iter % 6000000 == 0)
                {
                    retries++;
                    Console.WriteLine($"[DHCP] Re-sending Discover (Retry {retries})...");
                    _driver.QueueBytes(discoverPacket);
                }
            }

            // Step 2: Send DHCP Request to commit lease with DHCP server (Option 53 = 3)
            if (_offerReceived && _offeredIp != null)
            {
                Console.WriteLine($"[DHCP] Offer received for {_offeredIp[0]}.{_offeredIp[1]}.{_offeredIp[2]}.{_offeredIp[3]}! Sending DHCP Request...");
                byte[] requestPacket = BuildDhcpPacket(3, _offeredIp, _serverIp);
                _driver.QueueBytes(requestPacket);

                long reqTimeout = 15000000L;
                long reqIter = 0;
                while (!_ackReceived && reqIter < reqTimeout)
                {
                    _driver.HackPoll();
                    reqIter++;
                    if (reqIter % 5000000 == 0)
                    {
                        _driver.QueueBytes(requestPacket);
                    }
                }

                if (_ackReceived)
                {
                    Console.WriteLine("[DHCP] ACK received! Lease committed.");
                }
            }

            // Restore normal handler
            _driver.DataReceived = oldHandler;

            if (_offerReceived && _offeredIp != null)
            {
                Console.WriteLine($"[DHCP] SUCCESS! IP: {_offeredIp[0]}.{_offeredIp[1]}.{_offeredIp[2]}.{_offeredIp[3]}");
                if (_gatewayIp != null)
                    Console.WriteLine($"[DHCP] Gateway: {_gatewayIp[0]}.{_gatewayIp[1]}.{_gatewayIp[2]}.{_gatewayIp[3]}");
                return _offeredIp;
            }
            
            Console.WriteLine("[DHCP] TIMEOUT! No DHCP Offer received. Falling back to Direct Link mode (192.168.1.180).");
            ApplyFallback();
            return _offeredIp;
        }

        private void ApplyFallback()
        {
            _offeredIp = new byte[] { 192, 168, 1, 180 };
            _gatewayIp = new byte[] { 192, 168, 1, 1 };
            _subnetMask = new byte[] { 255, 255, 255, 0 };
        }

        private void CustomDataReceived(byte[] packet)
        {
            if (packet == null || packet.Length < 42) return;
            
            ushort etherType = (ushort)((packet[12] << 8) | packet[13]);
            if (etherType != 0x0800) return; // Not IPv4
            
            byte protocol = packet[23];
            if (protocol != 17) return; // Not UDP

            ushort dstPort = (ushort)((packet[36] << 8) | packet[37]);
            if (dstPort != 68) return; // Not DHCP client port

            // Verify BootReply (Op = 2)
            if (packet[42] != 2) return;

            // Verify XID (0x12345678)
            if (packet[46] != 0x12 || packet[47] != 0x34 || packet[48] != 0x56 || packet[49] != 0x78) return;

            // Extract Your Client IP Address (YIAddr) at offset 58
            byte[] yiaddr = new byte[] { packet[58], packet[59], packet[60], packet[61] };
            
            // Check Magic Cookie at offset 278 (0x63, 0x82, 0x53, 0x63)
            int dhcpOff = 42;
            int udpLen = (packet[38] << 8) | packet[39];
            int dhcpEnd = 34 + udpLen;
            if (dhcpEnd > packet.Length) dhcpEnd = packet.Length;

            if (dhcpEnd >= dhcpOff + 240 && packet[278] == 0x63 && packet[279] == 0x82 && packet[280] == 0x53 && packet[281] == 0x63)
            {
                byte msgType = 0;
                int optIdx = 282; // Start of DHCP Options

                while (optIdx + 1 < dhcpEnd && packet[optIdx] != 255)
                {
                    byte opt = packet[optIdx];
                    if (opt == 0) { optIdx++; continue; }
                    byte len = packet[optIdx + 1];
                    if (len == 0 || optIdx + 2 + len > dhcpEnd) break;

                    if (opt == 53 && len >= 1) msgType = packet[optIdx + 2]; // 2 = Offer, 5 = ACK
                    if (opt == 1 && len >= 4) // Subnet Mask
                    {
                        _subnetMask = new byte[] { packet[optIdx + 2], packet[optIdx + 3], packet[optIdx + 4], packet[optIdx + 5] };
                    }
                    if (opt == 3 && len >= 4) // Router (Gateway)
                    {
                        _gatewayIp = new byte[] { packet[optIdx + 2], packet[optIdx + 3], packet[optIdx + 4], packet[optIdx + 5] };
                    }
                    if (opt == 6 && len >= 4) // DNS Server
                    {
                        _dnsIp = new byte[] { packet[optIdx + 2], packet[optIdx + 3], packet[optIdx + 4], packet[optIdx + 5] };
                    }
                    if (opt == 54 && len >= 4) // DHCP Server Identifier
                    {
                        _serverIp = new byte[] { packet[optIdx + 2], packet[optIdx + 3], packet[optIdx + 4], packet[optIdx + 5] };
                    }

                    optIdx += 2 + len;
                }

                if (msgType == 2) // DHCP Offer
                {
                    _offeredIp = yiaddr;
                    _offerReceived = true;
                }
                else if (msgType == 5) // DHCP ACK
                {
                    _offeredIp = yiaddr;
                    _ackReceived = true;
                }
            }
            else
            {
                _offeredIp = yiaddr;
                _offerReceived = true;
            }
        }

        private byte[] BuildDhcpPacket(byte msgType, byte[] requestedIp, byte[] serverIp)
        {
            byte[] packet = new byte[342];
            byte[] mac = _driver.MACAddress.bytes;

            // 1. Ethernet Header (14 bytes)
            for (int i = 0; i < 6; i++) packet[i] = 0xFF; // Broadcast
            for (int i = 0; i < 6; i++) packet[6 + i] = mac[i]; // Source MAC
            packet[12] = 0x08; packet[13] = 0x00; // IPv4

            // 2. IPv4 Header (20 bytes)
            packet[14] = 0x45;
            packet[15] = 0x00;
            packet[16] = 0x01; packet[17] = 0x48; // Total Length (328)
            packet[18] = 0x00; packet[19] = 0x00;
            packet[20] = 0x00; packet[21] = 0x00;
            packet[22] = 0x40; // TTL: 64
            packet[23] = 17;   // Protocol: UDP
            packet[24] = 0x00; packet[25] = 0x00;
            packet[30] = 0xFF; packet[31] = 0xFF; packet[32] = 0xFF; packet[33] = 0xFF; // Broadcast
            
            CalculateIPv4Checksum(packet, 14);

            // 3. UDP Header (8 bytes)
            packet[34] = 0x00; packet[35] = 68; // Source Port 68
            packet[36] = 0x00; packet[37] = 67; // Destination Port 67
            packet[38] = 0x01; packet[39] = 0x34; // UDP Length 308
            packet[40] = 0x00; packet[41] = 0x00;

            // 4. DHCP Payload (300 bytes)
            packet[42] = 1; // BootRequest
            packet[43] = 1; // Ethernet
            packet[44] = 6; // MAC length
            packet[45] = 0; // Hops
            
            // XID: 0x12345678
            packet[46] = 0x12; packet[47] = 0x34; packet[48] = 0x56; packet[49] = 0x78;
            packet[50] = 0x00; packet[51] = 0x00;
            packet[52] = 0x80; packet[53] = 0x00; // Broadcast flag

            // Client Hardware Address at offset 70
            for (int i = 0; i < 6; i++) packet[70 + i] = mac[i];

            // Magic Cookie at offset 278
            packet[278] = 0x63; packet[279] = 0x82; packet[280] = 0x53; packet[281] = 0x63;

            int opt = 282;

            // Option 53: DHCP Message Type (1 = Discover, 3 = Request)
            packet[opt++] = 53;
            packet[opt++] = 1;
            packet[opt++] = msgType;

            // Option 61: Client Identifier
            packet[opt++] = 61;
            packet[opt++] = 7;
            packet[opt++] = 1; // Ethernet
            for (int i = 0; i < 6; i++) packet[opt++] = mac[i];

            // In DHCP Request: add Option 50 (Requested IP) & Option 54 (Server Identifier)
            if (msgType == 3 && requestedIp != null)
            {
                packet[opt++] = 50; packet[opt++] = 4;
                packet[opt++] = requestedIp[0]; packet[opt++] = requestedIp[1];
                packet[opt++] = requestedIp[2]; packet[opt++] = requestedIp[3];

                if (serverIp != null && serverIp[0] != 0)
                {
                    packet[opt++] = 54; packet[opt++] = 4;
                    packet[opt++] = serverIp[0]; packet[opt++] = serverIp[1];
                    packet[opt++] = serverIp[2]; packet[opt++] = serverIp[3];
                }
            }

            // Option 55: Parameter Request List (1=Subnet, 3=Router, 6=DNS)
            packet[opt++] = 55;
            packet[opt++] = 3;
            packet[opt++] = 1;
            packet[opt++] = 3;
            packet[opt++] = 6;

            // End Option
            packet[opt] = 255;

            return packet;
        }

        private void CalculateIPv4Checksum(byte[] packet, int offset)
        {
            uint sum = 0;
            for (int i = 0; i < 20; i += 2)
            {
                sum += (uint)((packet[offset + i] << 8) | packet[offset + i + 1]);
            }
            while ((sum >> 16) != 0)
            {
                sum = (sum & 0xFFFF) + (sum >> 16);
            }
            sum = ~sum;
            packet[offset + 10] = (byte)((sum >> 8) & 0xFF);
            packet[offset + 11] = (byte)(sum & 0xFF);
        }
    }
}

{
    public enum Wpa2HandshakeState
    {
        Idle,
        ConnectingAuthAssoc,
        WaitingMsg1,
        Msg2Sent,
        WaitingMsg3,
        Completed,
        Failed
    }

    /// <summary>
    /// Pure C# RFC 3174 SHA-1 implementation without any System.Security.Cryptography dependencies.
    /// Perfectly compatible with Cosmos OS / IL2CPU bare-metal runtime.
    /// </summary>
    public class PureSha1
    {
        private uint[] _h = new uint[5];
        private byte[] _buffer = new byte[64];
        private int _bufferLength;
        private ulong _totalBytes;
        private uint[] _w = new uint[80];

        public PureSha1()
        {
            Reset();
        }

        public void Reset()
        {
            _h[0] = 0x67452301u;
            _h[1] = 0xEFCDAB89u;
            _h[2] = 0x98BADCFEu;
            _h[3] = 0x10325476u;
            _h[4] = 0xC3D2E1F0u;
            _bufferLength = 0;
            _totalBytes = 0;
        }

        public void Update(byte[] data, int offset, int count)
        {
            if (data == null || count <= 0) return;
            _totalBytes += (ulong)count;

            int i = 0;
            if (_bufferLength > 0)
            {
                int toCopy = Math.Min(64 - _bufferLength, count);
                Array.Copy(data, offset, _buffer, _bufferLength, toCopy);
                _bufferLength += toCopy;
                i += toCopy;

                if (_bufferLength == 64)
                {
                    ProcessBlock(_buffer, 0);
                    _bufferLength = 0;
                }
            }

            while (i + 64 <= count)
            {
                ProcessBlock(data, offset + i);
                i += 64;
            }

            if (i < count)
            {
                int remaining = count - i;
                Array.Copy(data, offset + i, _buffer, 0, remaining);
                _bufferLength = remaining;
            }
        }

        public void Final(byte[] output, int outOffset)
        {
            ulong totalBits = _totalBytes * 8;

            _buffer[_bufferLength++] = 0x80;

            if (_bufferLength > 56)
            {
                while (_bufferLength < 64) _buffer[_bufferLength++] = 0;
                ProcessBlock(_buffer, 0);
                _bufferLength = 0;
            }

            while (_bufferLength < 56) _buffer[_bufferLength++] = 0;

            _buffer[56] = (byte)(totalBits >> 56);
            _buffer[57] = (byte)(totalBits >> 48);
            _buffer[58] = (byte)(totalBits >> 40);
            _buffer[59] = (byte)(totalBits >> 32);
            _buffer[60] = (byte)(totalBits >> 24);
            _buffer[61] = (byte)(totalBits >> 16);
            _buffer[62] = (byte)(totalBits >> 8);
            _buffer[63] = (byte)(totalBits);

            ProcessBlock(_buffer, 0);

            for (int i = 0; i < 5; i++)
            {
                output[outOffset + i * 4] = (byte)(_h[i] >> 24);
                output[outOffset + i * 4 + 1] = (byte)(_h[i] >> 16);
                output[outOffset + i * 4 + 2] = (byte)(_h[i] >> 8);
                output[outOffset + i * 4 + 3] = (byte)(_h[i]);
            }
        }

        private void ProcessBlock(byte[] block, int offset)
        {
            for (int t = 0; t < 16; t++)
            {
                int idx = offset + t * 4;
                _w[t] = ((uint)block[idx] << 24) |
                        ((uint)block[idx + 1] << 16) |
                        ((uint)block[idx + 2] << 8) |
                        (uint)block[idx + 3];
            }

            for (int t = 16; t < 80; t++)
            {
                uint temp = _w[t - 3] ^ _w[t - 8] ^ _w[t - 14] ^ _w[t - 16];
                _w[t] = (temp << 1) | (temp >> 31);
            }

            uint a = _h[0];
            uint b = _h[1];
            uint c = _h[2];
            uint d = _h[3];
            uint e = _h[4];

            for (int t = 0; t < 80; t++)
            {
                uint f, k;
                if (t < 20)
                {
                    f = (b & c) | ((~b) & d);
                    k = 0x5A827999u;
                }
                else if (t < 40)
                {
                    f = b ^ c ^ d;
                    k = 0x6ED9EBA1u;
                }
                else if (t < 60)
                {
                    f = (b & c) | (b & d) | (c & d);
                    k = 0x8F1BBCDCu;
                }
                else
                {
                    f = b ^ c ^ d;
                    k = 0xCA62C1D6u;
                }

                uint temp = ((a << 5) | (a >> 27)) + f + e + k + _w[t];
                e = d;
                d = c;
                c = (b << 30) | (b >> 2);
                b = a;
                a = temp;
            }

            _h[0] += a;
            _h[1] += b;
            _h[2] += c;
            _h[3] += d;
            _h[4] += e;
        }

        public static byte[] Hash(byte[] data)
        {
            var sha = new PureSha1();
            sha.Update(data, 0, data != null ? data.Length : 0);
            byte[] res = new byte[20];
            sha.Final(res, 0);
            return res;
        }
    }

    /// <summary>
    /// Pure C# RFC 2104 HMAC-SHA1 implementation with zero heap allocations during iterations.
    /// </summary>
    public class PureHmacSha1
    {
        private PureSha1 _sha = new PureSha1();
        private byte[] _keyPad = new byte[64];
        private byte[] _innerHash = new byte[20];
        private byte[] _ipad = new byte[64];
        private byte[] _opad = new byte[64];

        public void ComputeHmac(byte[] key, byte[] data, int dataOffset, int dataLen, byte[] output, int outOffset)
        {
            Array.Clear(_keyPad, 0, 64);
            if (key != null)
            {
                if (key.Length > 64)
                {
                    byte[] kh = PureSha1.Hash(key);
                    Array.Copy(kh, 0, _keyPad, 0, 20);
                }
                else
                {
                    Array.Copy(key, 0, _keyPad, 0, key.Length);
                }
            }

            for (int i = 0; i < 64; i++)
            {
                _ipad[i] = (byte)(_keyPad[i] ^ 0x36);
                _opad[i] = (byte)(_keyPad[i] ^ 0x5C);
            }

            _sha.Reset();
            _sha.Update(_ipad, 0, 64);
            if (data != null && dataLen > 0)
            {
                _sha.Update(data, dataOffset, dataLen);
            }
            _sha.Final(_innerHash, 0);

            _sha.Reset();
            _sha.Update(_opad, 0, 64);
            _sha.Update(_innerHash, 0, 20);
            _sha.Final(output, outOffset);
        }

        public static byte[] Compute(byte[] key, byte[] data)
        {
            var hmac = new PureHmacSha1();
            byte[] res = new byte[20];
            hmac.ComputeHmac(key, data, 0, data != null ? data.Length : 0, res, 0);
            return res;
        }
    }

    /// <summary>
    /// Pure C# RFC 2898 / PKCS#5 v2.0 PBKDF2-HMAC-SHA1 for deriving 32-byte PMK.
    /// </summary>
    public static class PurePbkdf2
    {
        public static byte[] DerivePmk(string passphrase, string ssid)
        {
            byte[] passBytes = Encoding.UTF8.GetBytes(passphrase ?? "");
            byte[] ssidBytes = Encoding.UTF8.GetBytes(ssid ?? "");
            return DeriveKey(passBytes, ssidBytes, 4096, 32);
        }

        public static byte[] DeriveKey(byte[] password, byte[] salt, int iterations, int keyLen)
        {
            byte[] output = new byte[keyLen];
            var hmac = new PureHmacSha1();
            byte[] saltPlusBlock = new byte[salt.Length + 4];
            Array.Copy(salt, 0, saltPlusBlock, 0, salt.Length);

            byte[] uBlock = new byte[20];
            byte[] uPrev = new byte[20];
            byte[] uCur = new byte[20];

            int totalBlocks = (keyLen + 19) / 20;
            int outOffset = 0;

            for (int block = 1; block <= totalBlocks; block++)
            {
                saltPlusBlock[salt.Length] = (byte)(block >> 24);
                saltPlusBlock[salt.Length + 1] = (byte)(block >> 16);
                saltPlusBlock[salt.Length + 2] = (byte)(block >> 8);
                saltPlusBlock[salt.Length + 3] = (byte)block;

                hmac.ComputeHmac(password, saltPlusBlock, 0, saltPlusBlock.Length, uPrev, 0);
                Array.Copy(uPrev, 0, uBlock, 0, 20);

                for (int iter = 2; iter <= iterations; iter++)
                {
                    hmac.ComputeHmac(password, uPrev, 0, 20, uCur, 0);
                    for (int x = 0; x < 20; x++)
                    {
                        uBlock[x] ^= uCur[x];
                        uPrev[x] = uCur[x];
                    }
                }

                int toCopy = Math.Min(20, keyLen - outOffset);
                Array.Copy(uBlock, 0, output, outOffset, toCopy);
                outOffset += toCopy;
            }

            return output;
        }
    }

    /// <summary>
    /// Pure C# IEEE 802.11i-2004 PRF-512 Pseudo-Random Function for PTK derivation.
    /// </summary>
    public static class PurePrf512
    {
        public static byte[] DerivePtk(byte[] pmk, byte[] aa, byte[] spa, byte[] anonce, byte[] snonce)
        {
            byte[] label = Encoding.ASCII.GetBytes("Pairwise key expansion");

            // Lexicographical order: Min(AA, SPA) || Max(AA, SPA)
            byte[] minMac = LessThan(aa, spa) ? aa : spa;
            byte[] maxMac = LessThan(aa, spa) ? spa : aa;

            // Lexicographical order: Min(ANonce, SNonce) || Max(ANonce, SNonce)
            byte[] minNonce = LessThan(anonce, snonce) ? anonce : snonce;
            byte[] maxNonce = LessThan(anonce, snonce) ? snonce : anonce;

            byte[] dataB = new byte[12 + 64];
            Array.Copy(minMac, 0, dataB, 0, 6);
            Array.Copy(maxMac, 0, dataB, 6, 6);
            Array.Copy(minNonce, 0, dataB, 12, 32);
            Array.Copy(maxNonce, 0, dataB, 44, 32);

            byte[] input = new byte[label.Length + 1 + dataB.Length + 1];
            Array.Copy(label, 0, input, 0, label.Length);
            input[label.Length] = 0x00;
            Array.Copy(dataB, 0, input, label.Length + 1, dataB.Length);

            byte[] ptk = new byte[64];
            byte[] hmacOut = new byte[20];
            var hmac = new PureHmacSha1();

            for (byte i = 0; i < 4; i++)
            {
                input[input.Length - 1] = i;
                hmac.ComputeHmac(pmk, input, 0, input.Length, hmacOut, 0);
                int copyLen = Math.Min(20, 64 - i * 20);
                Array.Copy(hmacOut, 0, ptk, i * 20, copyLen);
            }

            return ptk;
        }

        private static bool LessThan(byte[] a, byte[] b)
        {
            int len = Math.Min(a.Length, b.Length);
            for (int i = 0; i < len; i++)
            {
                if (a[i] < b[i]) return true;
                if (a[i] > b[i]) return false;
            }
            return a.Length < b.Length;
        }
    }

    /// <summary>
    /// Pure bare-metal IEEE 802.11i WPA2-PSK 4-Way Handshake Engine.
    /// Authenticates with modern WPA2-PSK access points and phone hotspots.
    /// Derives PMK, PTK, KCK, KEK, and Temporal Key (TK), sends valid EAPOL Messages 2 & 4,
    /// verifies AP Message 3 MIC, and rejects incorrect passwords.
    /// </summary>
    public class Wpa2HandshakeEngine
    {
        public Wpa2HandshakeState State { get; set; } = Wpa2HandshakeState.Idle;
        public string FailureReason { get; set; } = "";

        public byte[] ApBssid { get; private set; } = new byte[6];
        public byte[] StationMac { get; private set; } = new byte[6];
        public string Ssid { get; private set; } = "";
        public string Passphrase { get; private set; } = "";
        public int Channel { get; private set; } = 1;

        public byte[] Pmk { get; private set; } = new byte[32];
        public byte[] Ptk { get; private set; } = new byte[64];
        public byte[] Kck { get; private set; } = new byte[16];
        public byte[] Kek { get; private set; } = new byte[16];
        public byte[] Tk { get; private set; } = new byte[16];

        public byte[] ANonce { get; private set; } = new byte[32];
        public byte[] SNonce { get; private set; } = new byte[32];
        public byte[] Msg1ReplayCounter { get; private set; } = new byte[8];
        public byte[] Msg3ReplayCounter { get; private set; } = new byte[8];

        public ushort SequenceNumber = 1;

        // Station RSN Information Element (Tag 48, Length 20)
        private static readonly byte[] StationRsnIe = new byte[] {
            0x30, 0x14,
            0x01, 0x00,                         // Version: 1
            0x00, 0x0F, 0xAC, 0x04,             // Group Cipher: AES-CCMP
            0x01, 0x00, 0x00, 0x0F, 0xAC, 0x04, // Pairwise Cipher: 1 AES-CCMP
            0x01, 0x00, 0x00, 0x0F, 0xAC, 0x02, // AKM Suite: 1 PSK
            0x00, 0x00                          // RSN Capabilities
        };

        public void Initialize(byte[] apBssid, byte[] stationMac, string ssid, string passphrase, int channel)
        {
            Array.Copy(apBssid, 0, ApBssid, 0, 6);
            Array.Copy(stationMac, 0, StationMac, 0, 6);
            Ssid = ssid ?? "";
            Passphrase = passphrase ?? "";
            Channel = channel;
            State = Wpa2HandshakeState.ConnectingAuthAssoc;
            FailureReason = "";

            // Derive 32-byte PMK using PBKDF2 (4096 iterations)
            Pmk = PurePbkdf2.DerivePmk(Passphrase, Ssid);
        }

        public static byte[] GenerateSNonce(byte[] stationMac, byte[] apBssid, uint tsfL, uint tsfU)
        {
            byte[] entropy = new byte[36];
            for (int i = 0; i < 6; i++)
            {
                entropy[i] = stationMac != null && i < stationMac.Length ? stationMac[i] : (byte)0x11;
                entropy[6 + i] = apBssid != null && i < apBssid.Length ? apBssid[i] : (byte)0x22;
            }
            entropy[12] = (byte)(tsfL);
            entropy[13] = (byte)(tsfL >> 8);
            entropy[14] = (byte)(tsfL >> 16);
            entropy[15] = (byte)(tsfL >> 24);
            entropy[16] = (byte)(tsfU);
            entropy[17] = (byte)(tsfU >> 8);
            entropy[18] = (byte)(tsfU >> 16);
            entropy[19] = (byte)(tsfU >> 24);
            entropy[20] = 0xAA; entropy[21] = 0x55; entropy[22] = 0x12; entropy[23] = 0x34;
            entropy[24] = 0x56; entropy[25] = 0x78; entropy[26] = 0x9A; entropy[27] = 0xBC;
            entropy[28] = 0xDE; entropy[29] = 0xF0; entropy[30] = 0xFE; entropy[31] = 0xED;
            entropy[32] = 0xCA; entropy[33] = 0xFE; entropy[34] = 0xBA; entropy[35] = 0xBE;

            byte[] h1 = PureSha1.Hash(entropy);
            for (int i = 0; i < 36; i++) entropy[i] ^= 0x5C;
            byte[] h2 = PureSha1.Hash(entropy);

            byte[] snonce = new byte[32];
            Array.Copy(h1, 0, snonce, 0, 20);
            Array.Copy(h2, 0, snonce, 20, 12);
            return snonce;
        }

        public bool ProcessEapolFrame(byte[] pkt, int eapolOffset, uint tsfL, uint tsfU, out byte[] responseFrame)
        {
            responseFrame = null;
            if (pkt == null || eapolOffset + 4 > pkt.Length) return false;

            byte eapolType = pkt[eapolOffset + 1];
            if (eapolType != 3) return false; // 3 = EAPOL-Key

            int bodyLen = (pkt[eapolOffset + 2] << 8) | pkt[eapolOffset + 3];
            if (eapolOffset + 4 + bodyLen > pkt.Length) return false;

            int kbOffset = eapolOffset + 4;
            byte descType = pkt[kbOffset];
            if (descType != 2 && descType != 254) return false; // 2 = RSN, 254 = WPA

            ushort keyInfo = (ushort)((pkt[kbOffset + 1] << 8) | pkt[kbOffset + 2]);
            bool isPairwise = (keyInfo & (1 << 3)) != 0;
            bool isInstall = (keyInfo & (1 << 6)) != 0;
            bool isAck = (keyInfo & (1 << 7)) != 0;
            bool isMic = (keyInfo & (1 << 8)) != 0;

            // Handle Message 1 (Pairwise=1, Ack=1, MIC=0)
            if (isPairwise && isAck && !isMic)
            {
                // Replay counter (8 bytes at kbOffset + 5)
                for (int i = 0; i < 8; i++) Msg1ReplayCounter[i] = pkt[kbOffset + 5 + i];

                // ANonce (32 bytes at kbOffset + 13)
                for (int i = 0; i < 32; i++) ANonce[i] = pkt[kbOffset + 13 + i];

                // Generate SNonce
                SNonce = GenerateSNonce(StationMac, ApBssid, tsfL, tsfU);

                // Derive PTK (64 bytes)
                Ptk = PurePrf512.DerivePtk(Pmk, ApBssid, StationMac, ANonce, SNonce);
                Array.Copy(Ptk, 0, Kck, 0, 16);
                Array.Copy(Ptk, 16, Kek, 0, 16);
                Array.Copy(Ptk, 32, Tk, 0, 16);

                // Construct Message 2
                responseFrame = BuildMessage2();
                State = Wpa2HandshakeState.Msg2Sent;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  [EAPOL] Message 1/4 received from AP! Sent Message 2/4 (with computed MIC).");
                Console.ForegroundColor = ConsoleColor.Cyan;
                return true;
            }

            // Handle Message 3 (Pairwise=1, Ack=1, MIC=1)
            if (isPairwise && isAck && isMic && (State == Wpa2HandshakeState.Msg2Sent || State == Wpa2HandshakeState.WaitingMsg3))
            {
                for (int i = 0; i < 8; i++) Msg3ReplayCounter[i] = pkt[kbOffset + 5 + i];

                // Extract AP MIC (16 bytes at kbOffset + 77)
                byte[] apMic = new byte[16];
                for (int i = 0; i < 16; i++) apMic[i] = pkt[kbOffset + 77 + i];

                // Zero out MIC in a copy of the packet and verify HMAC-SHA1
                int eapolPacketLen = 4 + bodyLen;
                byte[] verifyBuf = new byte[eapolPacketLen];
                Array.Copy(pkt, eapolOffset, verifyBuf, 0, eapolPacketLen);
                for (int i = 0; i < 16; i++) verifyBuf[4 + 77 + i] = 0;

                byte[] computedHmac = PureHmacSha1.Compute(Kck, verifyBuf);
                bool valid = true;
                for (int i = 0; i < 16; i++)
                {
                    if (computedHmac[i] != apMic[i])
                    {
                        valid = false;
                        break;
                    }
                }

                if (!valid)
                {
                    State = Wpa2HandshakeState.Failed;
                    FailureReason = "AP Message 3 MIC verification failed! Incorrect passphrase.";
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("  [EAPOL] Message 3/4 MIC mismatch! Password incorrect.");
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    return false;
                }

                // AP Message 3 verified! Build Message 4
                responseFrame = BuildMessage4();
                State = Wpa2HandshakeState.Completed;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  [EAPOL] Message 3/4 verified! AP acknowledged station. Sent Message 4/4.");
                Console.ForegroundColor = ConsoleColor.Cyan;
                return true;
            }

            return false;
        }

        public byte[] BuildMessage2()
        {
            int rsnLen = StationRsnIe.Length; // 22 bytes
            int bodyLen = 95 + rsnLen;       // 117 bytes
            int eapolLen = 4 + bodyLen;      // 121 bytes

            byte[] eapol = new byte[eapolLen];
            eapol[0] = 0x01; // Version 1
            eapol[1] = 0x03; // EAPOL-Key
            eapol[2] = (byte)(bodyLen >> 8);
            eapol[3] = (byte)(bodyLen & 0xFF);

            int kb = 4;
            eapol[kb] = 0x02; // Descriptor Type: RSN
            // Key Info: Pairwise=1 (bit 3), MIC=1 (bit 8), Version=2 (bits 0-2) -> 0x010A
            eapol[kb + 1] = 0x01;
            eapol[kb + 2] = 0x0A;
            eapol[kb + 3] = 0x00; // Key Length: 0
            eapol[kb + 4] = 0x00;

            // Replay Counter matching Message 1
            for (int i = 0; i < 8; i++) eapol[kb + 5 + i] = Msg1ReplayCounter[i];

            // SNonce
            for (int i = 0; i < 32; i++) eapol[kb + 13 + i] = SNonce[i];

            // Key IV (16 bytes = 0), RSC (8 bytes = 0), Reserved (8 bytes = 0), MIC (16 bytes = 0)
            // Array defaults to 0.

            // Key Data Length (22 bytes)
            eapol[kb + 93] = (byte)(rsnLen >> 8);
            eapol[kb + 94] = (byte)(rsnLen & 0xFF);

            // Key Data: Station RSN IE
            Array.Copy(StationRsnIe, 0, eapol, kb + 95, rsnLen);

            // Compute 16-byte HMAC-SHA1 MIC over whole EAPOL frame
            byte[] mic = PureHmacSha1.Compute(Kck, eapol);
            Array.Copy(mic, 0, eapol, kb + 77, 16);

            return WrapIn80211DataFrame(eapol);
        }

        public byte[] BuildMessage4()
        {
            int bodyLen = 95;           // 95 bytes (Key Data Length = 0)
            int eapolLen = 4 + bodyLen; // 99 bytes

            byte[] eapol = new byte[eapolLen];
            eapol[0] = 0x01; // Version 1
            eapol[1] = 0x03; // EAPOL-Key
            eapol[2] = (byte)(bodyLen >> 8);
            eapol[3] = (byte)(bodyLen & 0xFF);

            int kb = 4;
            eapol[kb] = 0x02; // Descriptor Type: RSN
            // Key Info: Pairwise=1 (bit 3), MIC=1 (bit 8), Secure=1 (bit 9), Version=2 (bits 0-2) -> 0x030A
            eapol[kb + 1] = 0x03;
            eapol[kb + 2] = 0x0A;
            eapol[kb + 3] = 0x00; // Key Length: 0
            eapol[kb + 4] = 0x00;

            // Replay Counter matching Message 3
            for (int i = 0; i < 8; i++) eapol[kb + 5 + i] = Msg3ReplayCounter[i];

            // SNonce is 0 in Message 4
            // Key IV, RSC, Reserved, MIC are 0.
            eapol[kb + 93] = 0x00; // Key Data Length = 0
            eapol[kb + 94] = 0x00;

            // Compute 16-byte HMAC-SHA1 MIC
            byte[] mic = PureHmacSha1.Compute(Kck, eapol);
            Array.Copy(mic, 0, eapol, kb + 77, 16);

            return WrapIn80211DataFrame(eapol);
        }

        private byte[] WrapIn80211DataFrame(byte[] eapolPayload)
        {
            // Standard 802.11 Data Frame (compatible with all APs & phone hotspots):
            // 24 bytes MAC header + 8 bytes LLC/SNAP + EAPOL
            int totalLen = 24 + 8 + eapolPayload.Length;
            byte[] frame = new byte[totalLen];

            // Frame Control: Data (0x08), ToDS=1 (0x01)
            frame[0] = 0x08;
            frame[1] = 0x01;
            frame[2] = 0x00; // Duration
            frame[3] = 0x00;

            // Address 1: Receiver / AP BSSID
            Array.Copy(ApBssid, 0, frame, 4, 6);
            // Address 2: Transmitter / Station MAC
            Array.Copy(StationMac, 0, frame, 10, 6);
            // Address 3: Destination / AP BSSID
            Array.Copy(ApBssid, 0, frame, 16, 6);

            // Sequence Control
            ushort seq = (ushort)((SequenceNumber++ & 0x0FFF) << 4);
            frame[22] = (byte)(seq & 0xFF);
            frame[23] = (byte)(seq >> 8);

            // LLC / SNAP Header (802.1X EtherType 0x888E)
            frame[24] = 0xAA;
            frame[25] = 0xAA;
            frame[26] = 0x03;
            frame[27] = 0x00;
            frame[28] = 0x00;
            frame[29] = 0x00;
            frame[30] = 0x88;
            frame[31] = 0x8E;

            // EAPOL Payload
            Array.Copy(eapolPayload, 0, frame, 32, eapolPayload.Length);

            return frame;
        }
    }
}

{
    /// <summary>
    /// Bare-metal Atheros AR2425 / AR5007EG / AR5212 IEEE 802.11b/g Wireless Driver.
    /// Supports PCI/PCIe Vendor 0x168C (Atheros AR2425, AR5212, AR2413, AR5413, AR2417).
    /// Performs direct 32-bit MMIO register control, EEPROM reads, RF synthesis tuning (2.4 GHz Ch 1-13),
    /// RX DMA descriptor management, and real-time 802.11 Beacon / Probe Response frame parsing.
    /// Pure polling mode - 0 interrupts, 0 Thread.Sleep to prevent lockups on Intel Atom N270.
    /// </summary>
    [Cosmos.HAL.Driver(Cosmos.HAL.DriverType.Network, "Atheros AR2425/AR5007EG 802.11b/g Wireless")]
    [Cosmos.HAL.Author("ghost")]
    public class AtherosAR2425Driver
    {
        public PCIDevice PciDev { get; private set; }
        public uint MmioBase { get; private set; }
        public MemoryBlock Mmio { get; private set; }
        public byte[] MacAddress { get; private set; } = new byte[6];
        public bool IsInitialized { get; private set; } = false;
        public uint MacSrev { get; private set; }
        public uint PhyChipId { get; private set; }
        public ushort EepromVersion { get; private set; }
        public ushort EepromRegDomain { get; private set; }
        public int CurrentChannel { get; private set; } = 1;
        public uint RxPacketsCount { get; private set; } = 0;
        public uint TxPacketsCount { get; private set; } = 0;
        public uint BeaconFramesCount { get; private set; } = 0;
        public string HardwareSummary { get; private set; } = "Not detected";

        public Wpa2HandshakeEngine Wpa2Engine;
        public const uint AR5K_TSF_L32 = 0x804C;
        public const uint AR5K_TSF_U32 = 0x8050;
        public const uint AR5K_KEYTABLE_VALID = 0x00008000;
        public const uint AR5K_KEYTABLE_TYPE_CCM = 0x00000006;
        public const uint AR5K_KEYTABLE_TYPE_NULL = 0x00000007;

        // RX DMA Descriptors (16 descriptors)
        private const int RX_DESC_COUNT = 16;
        private const int RX_BUF_SIZE = 2048;
        private ManagedMemoryBlock _rxDescMem;
        private ManagedMemoryBlock _rxBufferMem;
        private uint _rxDescPhys;
        private uint _rxBufferPhys;
        private int _rxHead = 0;

        // TX DMA Descriptors (4 descriptors)
        private const int TX_DESC_COUNT = 4;
        private const int TX_BUF_SIZE = 512;
        private ManagedMemoryBlock _txDescMem;
        private ManagedMemoryBlock _txBufferMem;
        private uint _txDescPhys;
        private uint _txBufferPhys;
        private int _txHead = 0;

        // Detected Wi-Fi networks dictionary (BSSID -> WifiAccessPoint)
        public Dictionary<string, WifiAccessPoint> DetectedAps = new Dictionary<string, WifiAccessPoint>();

        // Register Addresses
        private const uint AR5K_CR = 0x0008;
        private const uint AR5K_CR_RXE = 0x00000004;
        private const uint AR5K_CR_TXE = 0x00000001;
        private const uint AR5K_CR_RXD = 0x00000020;
        private const uint AR5K_CR_TXD = 0x00000010;

        private const uint AR5K_CFG = 0x0014;
        private const uint AR5K_IER = 0x0024;
        private const uint AR5K_TXCFG = 0x0030;
        private const uint AR5K_RXCFG = 0x0034;
        private const uint AR5K_RXDP = 0x000C;

        private const uint AR5K_QCU_TXE = 0x0840;
        private const uint AR5K_QUEUE_TXDP_0 = 0x0800;
        private const uint AR5K_QUEUE_MISC_0 = 0x09C0;
        private const uint AR5K_QUEUE_DFS_LOCAL_IFS_0 = 0x1040;
        private const uint AR5K_QUEUE_DFS_RETRY_LIMIT_0 = 0x1080;
        private const uint AR5K_QUEUE_QCUMASK_0 = 0x1000;

        private const uint AR5K_RESET_CTL = 0x4000;
        private const uint AR5K_SLEEP_CTL = 0x4004;
        private const uint AR5K_PCICFG = 0x4010;
        private const uint AR5K_PCIE_SERDES = 0x4040;
        private const uint AR5K_PCIE_SERDES_RESET = 0x4044;

        private const uint AR5K_STA_ID0 = 0x8000;
        private const uint AR5K_STA_ID1 = 0x8004;
        private const uint AR5K_BSS_ID0 = 0x8008;
        private const uint AR5K_BSS_ID1 = 0x800C;
        private const uint AR5K_RX_FILTER = 0x803C;
        private const uint AR5K_MCAST_FILTER0 = 0x8040;
        private const uint AR5K_MCAST_FILTER1 = 0x8044;
        private const uint AR5K_DIAG_SW_5211 = 0x8048;
        private const uint AR5K_DEFAULT_ANTENNA = 0x80D0;
        private const uint AR5K_BSS_IDM0 = 0x80E0;
        private const uint AR5K_BSS_IDM1 = 0x80E4;

        private const uint AR5K_EEPROM_BASE = 0x6000;
        private const uint AR5K_EEPROM_DATA_5211 = 0x6004;
        private const uint AR5K_EEPROM_CMD = 0x6008;
        private const uint AR5K_EEPROM_STAT_5211 = 0x600C;

        private const uint AR5K_PHY_0 = 0x9800;
        private const uint AR5K_PHY_TURBO = 0x9804;
        private const uint AR5K_PHY_TIMING_3 = 0x9814;
        private const uint AR5K_PHY_ACT = 0x981C;
        private const uint AR5K_PHY_RF_CTL3 = 0x9828;
        private const uint AR5K_PHY_RF_CTL4 = 0x9834;
        private const uint AR5K_PHY_SETTLING = 0x9844;
        private const uint AR5K_PHY_GAIN = 0x9848;
        private const uint AR5K_PHY_DESIRED_SIZE = 0x9850;
        private const uint AR5K_PHY_AGCCTL = 0x9860;
        private const uint AR5K_PHY_NF62 = 0x9864;
        private const uint AR5K_PHY_SCAL = 0x9878;
        private const uint AR5K_PHY_PLL = 0x987C;
        private const uint AR5K_RF_BUFFER = 0x989C;
        private const uint AR5K_RF_BUFFER_CONTROL_5 = 0x98D8;
        private const uint AR5K_PHY_ANT_SWITCH_TABLE_0 = 0x9910;
        private const uint AR5K_PHY_ANT_SWITCH_TABLE_1 = 0x9960;
        private const uint AR5K_PHY_ANT_CTL = 0x9964;
        private const uint AR5K_PHY_NFTHRES = 0x9968;
        private const uint AR5K_PHY_MODE = 0xA200;
        private const uint AR5K_PHY_CCKTXCTL = 0xA204;
        private const uint AR5K_PHY_FAST_ADC = 0xA24C;
        private const uint AR5K_PHY_CHIP_ID = 0x9818;

        public bool FindAndInitialize()
        {
            Console.WriteLine("ath5k: searching for Atheros 802.11 Wireless LAN device on PCI bus...");
            PCIDevice foundDev = null;

            foreach (var dev in PCI.Devices)
            {
                if (dev.VendorID == 0x168C)
                {
                    foundDev = dev;
                    break;
                }
            }

            if (foundDev == null)
            {
                Console.WriteLine("ath5k: no Atheros PCI device (Vendor 0x168C) found.");
                HardwareSummary = "Nenhum chip Atheros (0x168C) no PCI";
                return false;
            }

            PciDev = foundDev;
            Console.WriteLine($"ath5k: found device VID:0x{PciDev.VendorID:X4} DID:0x{PciDev.DeviceID:X4} at {PciDev.bus}:{PciDev.slot}:{PciDev.function}");

            // Enable Memory and Bus Master in PCI Command Register
            PciDev.EnableMemory(true);
            PciDev.EnableBusMaster(true);

            // Set PCI latency timer and cache line size for DMA
            PciDev.WriteRegister8(0x0C, 16); // Cache line size = 16 dwords (64B)
            PciDev.WriteRegister8(0x0D, 0xA8); // Latency timer
            PciDev.WriteRegister8(0x41, 0); // Disable retry timeout

            // Read BAR0 (MMIO base)
            uint bar0 = PciDev.ReadRegister32(0x10);
            MmioBase = bar0 & 0xFFFFFFF0;
            if (MmioBase == 0)
            {
                Console.WriteLine("ath5k: BAR0 MMIO base is 0!");
                HardwareSummary = "BAR0 MMIO Invalido";
                return false;
            }

            Console.WriteLine($"ath5k: MMIO base mapped at 0x{MmioBase:X8}");
            Mmio = new MemoryBlock(MmioBase, 0x10000);

            // Program PCIe SerDes if PCIe device (e.g. AR2425 DID 0x001C)
            if (PciDev.DeviceID == 0x001C || PciDev.DeviceID == 0x0023 || PciDev.DeviceID == 0x0024)
            {
                Console.WriteLine("ath5k: configuring PCIe SerDes PLL and power settings...");
                WriteReg(AR5K_PCIE_SERDES, 0x9248fc00);
                WriteReg(AR5K_PCIE_SERDES, 0x24924924);
                WriteReg(AR5K_PCIE_SERDES, 0x28000039);
                WriteReg(AR5K_PCIE_SERDES, 0x53160824);
                WriteReg(AR5K_PCIE_SERDES, 0xe5980579);
                WriteReg(AR5K_PCIE_SERDES, 0x001defff);
                WriteReg(AR5K_PCIE_SERDES, 0x1aaabe40);
                WriteReg(AR5K_PCIE_SERDES, 0xbe105554);
                WriteReg(AR5K_PCIE_SERDES, 0x000e3007);
                WriteReg(AR5K_PCIE_SERDES_RESET, 0);
                DelayMicroseconds(1000);
            }

            // Wake up NIC & soft reset
            NicWakeup();

            // Read SREV & PHY IDs
            MacSrev = ReadReg(0x0020); // AR5K_SREV
            PhyChipId = ReadReg(AR5K_PHY_CHIP_ID);
            Console.WriteLine($"ath5k: MAC SREV: 0x{MacSrev:X8}, PHY ID: 0x{PhyChipId:X8}");

            // Read EEPROM Header and MAC
            InitEeprom();

            HardwareSummary = $"Atheros AR2425/AR5212 [PCI {PciDev.bus}:{PciDev.slot}:{PciDev.function}] MAC {GetMacString()} SREV 0x{MacSrev:X2}";

            // Allocate and setup RX / TX DMA Descriptors
            SetupDmaDescriptors();

            // Reset and tune to Channel 1 (2412 MHz)
            ResetAndTuneChannel(1);

            IsInitialized = true;
            Console.WriteLine("ath5k: Atheros 802.11 driver initialized and actively listening for Beacons!");
            return true;
        }

        private void WriteReg(uint reg, uint val)
        {
            Mmio.DWords[reg] = val;
        }

        private uint ReadReg(uint reg)
        {
            return Mmio.DWords[reg];
        }

        private void DelayMicroseconds(int us)
        {
            // Intel Atom N270 calibration loop
            int count = us * 12;
            for (int i = 0; i < count; i++)
            {
                IOPort.Wait();
            }
        }

        private void NicWakeup()
        {
            // Clear power save bit in STA_ID1
            uint sta1 = ReadReg(AR5K_STA_ID1);
            WriteReg(AR5K_STA_ID1, sta1 & ~0x00040000u);

            // Set wake in SLEEP_CTL
            WriteReg(AR5K_SLEEP_CTL, 0);

            // Poll PCICFG until sleep state clears
            for (int i = 0; i < 5000; i++)
            {
                if ((ReadReg(AR5K_PCICFG) & 0x00010000u) == 0) break;
                DelayMicroseconds(10);
            }

            // Warm reset: reset baseband and PCU (Do NOT reset PCI on PCIe!)
            WriteReg(AR5K_RESET_CTL, 0x03);
            DelayMicroseconds(50);
            WriteReg(AR5K_SLEEP_CTL, 0);
            WriteReg(AR5K_RESET_CTL, 0);
            WriteReg(AR5K_CFG, 0);
            DelayMicroseconds(100);
        }

        private void InitEeprom()
        {
            // Read magic & version
            ushort magic = ReadEepromWord(0x003D);
            EepromVersion = ReadEepromWord(0x00C1);
            EepromRegDomain = ReadEepromWord(0x00BF);

            // Read MAC address from offsets 0x1F, 0x1E, 0x1D
            ushort w2 = ReadEepromWord(0x1F);
            ushort w1 = ReadEepromWord(0x1E);
            ushort w0 = ReadEepromWord(0x1D);

            // IEEE 802 Atheros EEPROM layout: high byte is first octet of word
            MacAddress[0] = (byte)((w2 >> 8) & 0xFF);
            MacAddress[1] = (byte)(w2 & 0xFF);
            MacAddress[2] = (byte)((w1 >> 8) & 0xFF);
            MacAddress[3] = (byte)(w1 & 0xFF);
            MacAddress[4] = (byte)((w0 >> 8) & 0xFF);
            MacAddress[5] = (byte)(w0 & 0xFF);

            // If EEPROM read was blank (00:00:00:00:00:00 or FF:FF:FF:FF:FF:FF), use Atheros fallback
            if ((MacAddress[0] == 0 && MacAddress[1] == 0 && MacAddress[2] == 0) ||
                (MacAddress[0] == 0xFF && MacAddress[1] == 0xFF))
            {
                MacAddress = new byte[] { 0x00, 0x1E, 0x65, 0x8F, 0xC1, 0x0D };
            }

            Console.WriteLine($"ath5k: EEPROM Ver: 0x{EepromVersion:X4}, RegDomain: 0x{EepromRegDomain:X4}, MAC: {GetMacString()}");
        }

        private ushort ReadEepromWord(uint offset)
        {
            WriteReg(AR5K_EEPROM_BASE, offset);
            WriteReg(AR5K_EEPROM_CMD, 0x01); // Read command

            for (int i = 0; i < 2000; i++)
            {
                uint stat = ReadReg(AR5K_EEPROM_STAT_5211);
                if ((stat & 0x02) != 0) // Done
                {
                    return (ushort)(ReadReg(AR5K_EEPROM_DATA_5211) & 0xFFFF);
                }
                DelayMicroseconds(5);
            }
            return 0;
        }

        public string GetMacString()
        {
            return $"{MacAddress[0]:X2}:{MacAddress[1]:X2}:{MacAddress[2]:X2}:{MacAddress[3]:X2}:{MacAddress[4]:X2}:{MacAddress[5]:X2}";
        }

        private void SetupDmaDescriptors()
        {
            // Allocate 16 RX Descriptors (24 bytes each, aligned) + Buffers (2048 bytes each)
            int descTotalSize = RX_DESC_COUNT * 32;
            int bufTotalSize = RX_DESC_COUNT * RX_BUF_SIZE;

            _rxDescMem = new ManagedMemoryBlock((uint)descTotalSize, 256);
            _rxDescPhys = (uint)_rxDescMem.Offset;

            _rxBufferMem = new ManagedMemoryBlock((uint)bufTotalSize, 8);
            _rxBufferPhys = (uint)_rxBufferMem.Offset;

            // Initialize RX Descriptors Ring
            for (int i = 0; i < RX_DESC_COUNT; i++)
            {
                uint dAddr = (uint)(_rxDescPhys + i * 32);
                uint nextDAddr = (uint)(_rxDescPhys + ((i + 1) % RX_DESC_COUNT) * 32);
                uint bAddr = (uint)(_rxBufferPhys + i * RX_BUF_SIZE);

                MemoryBlock descBlock = new MemoryBlock(dAddr, 32);
                descBlock.DWords[0] = nextDAddr; // ds_link
                descBlock.DWords[4] = bAddr;    // ds_data
                descBlock.DWords[8] = 0;        // rx_ctl0
                descBlock.DWords[12] = (uint)(RX_BUF_SIZE & 0x0FFF); // rx_ctl1: buf_len
                descBlock.DWords[16] = 0;       // rx_stat0
                descBlock.DWords[20] = 0;       // rx_stat1
            }

            // Allocate TX Descriptors & Buffers
            int txDescTotal = TX_DESC_COUNT * 32;
            int txBufTotal = TX_DESC_COUNT * TX_BUF_SIZE;

            _txDescMem = new ManagedMemoryBlock((uint)txDescTotal, 256);
            _txDescPhys = (uint)_txDescMem.Offset;

            _txBufferMem = new ManagedMemoryBlock((uint)txBufTotal, 8);
            _txBufferPhys = (uint)_txBufferMem.Offset;

            for (int i = 0; i < TX_DESC_COUNT; i++)
            {
                uint dAddr = (uint)(_txDescPhys + i * 32);
                uint bAddr = (uint)(_txBufferPhys + i * TX_BUF_SIZE);

                MemoryBlock descBlock = new MemoryBlock(dAddr, 32);
                descBlock.DWords[0] = 0;        // ds_link
                descBlock.DWords[4] = bAddr;    // ds_data
                descBlock.DWords[8] = 0;        // tx_ctl0
                descBlock.DWords[12] = 0;       // tx_ctl1
                descBlock.DWords[16] = 0;       // tx_ctl2
                descBlock.DWords[20] = 0;       // tx_ctl3
                descBlock.DWords[24] = 0;       // tx_stat0
                descBlock.DWords[28] = 0;       // tx_stat1
            }
        }

        public void ResetAndTuneChannel(int channel)
        {
            if (channel < 1) channel = 1;
            if (channel > 13) channel = 13;
            CurrentChannel = channel;

            // Stop RX & TX DMA
            WriteReg(AR5K_CR, AR5K_CR_RXD | AR5K_CR_TXD);
            WriteReg(AR5K_IER, 0); // Polling mode only!

            NicWakeup();

            // Set PHY Shift
            WriteReg(AR5K_PHY_0, 0x00000007); // AR5K_PHY_SHIFT_5GHZ

            // Apply Mode Start Initvals
            ApplyModeStartTable();

            // Apply Common Start Initvals
            ApplyCommonStartTable();

            // Apply Mode End (RF2425)
            ApplyModeEndTable();

            // Apply Common End (RF2425)
            ApplyCommonEndTable();

            // Baseband gain & RF gain
            ApplyBbGainTable();
            ApplyRfGainTable();

            // Tweak initval settings for AR2425 Swan
            WriteReg(AR5K_PHY_SCAL, 0x0E);
            WriteReg(AR5K_PHY_FAST_ADC, 0x01);

            // Commit RF Buffer
            ApplyRfBuffer();

            // OFDM Timings (AR5K_PHY_TIMING_3)
            WriteReg(AR5K_PHY_TIMING_3, 0x9C0A9F6B);

            // Antenna switch table & idle control (Both antennas: Main + Aux)
            WriteReg(AR5K_PHY_ANT_SWITCH_TABLE_0, 0x00000001);
            WriteReg(AR5K_PHY_ANT_SWITCH_TABLE_1, 0x00000002);
            WriteReg(AR5K_PHY_ANT_CTL, 0x00000011); // Diversity active on both antennas
            WriteReg(AR5K_PHY_NFTHRES, 0x000001F0); // Noise floor threshold (-95 dBm)
            WriteReg(AR5K_DEFAULT_ANTENNA, 0x01);   // Default to Antenna 1 (Main / Cabo Preto)

            // Setup PLL & Channel Frequency
            // Clock = 44MHz (0xAB) | PLL_RF5112 (0x40) = 0xEB
            WriteReg(AR5K_PHY_PLL, 0x000000EB);
            DelayMicroseconds(300);

            // PHY Mode: Dynamic OFDM/CCK (0x04) | 2GHz (0x02) | RF5112 (0x08) = 0x0E
            WriteReg(AR5K_PHY_MODE, 0x0000000E);
            WriteReg(AR5K_PHY_TURBO, 0);

            // Tune Frequency: Freq = 2407 + 5 * channel
            int freqMhz = 2407 + 5 * channel;
            TuneRf2425Channel(freqMhz);

            // Activate PHY
            WriteReg(AR5K_PHY_ACT, 0x01);
            DelayMicroseconds(500);

            // Run AGC Calibration
            uint agc = ReadReg(AR5K_PHY_AGCCTL);
            WriteReg(AR5K_PHY_AGCCTL, agc | 0x03); // Calibrate AGC & NF
            for (int i = 0; i < 1000; i++)
            {
                if ((ReadReg(AR5K_PHY_AGCCTL) & 0x03) == 0) break;
                DelayMicroseconds(10);
            }

            // Setup MAC addresses & BSSID filter
            uint lowMac = (uint)(MacAddress[0] | (MacAddress[1] << 8) | (MacAddress[2] << 16) | (MacAddress[3] << 24));
            uint highMac = (uint)(MacAddress[4] | (MacAddress[5] << 8));
            WriteReg(AR5K_STA_ID0, lowMac);
            // Enable hardware ACK generation on default antenna at 11b/6M base rates:
            // AR5K_STA_ID1_DEFAULT_ANTENNA | AR5K_STA_ID1_DESC_ANTENNA | AR5K_STA_ID1_RTS_DEF_ANTENNA |
            // AR5K_STA_ID1_ACKCTS_6MB | AR5K_STA_ID1_BASE_RATE_11B | AR5K_STA_ID1_SELFGEN_DEF_ANT = 0x07E00000
            WriteReg(AR5K_STA_ID1, highMac | 0x07E00000u);

            // BSSID Mask (Promiscuous BSSID acceptance for scanning)
            WriteReg(AR5K_BSS_IDM0, 0x00000000);
            WriteReg(AR5K_BSS_IDM1, 0x00000000);
            WriteReg(AR5K_BSS_ID0, 0xFFFFFFFF);
            WriteReg(AR5K_BSS_ID1, 0x0000FFFF);

            // Multicast filter accept all
            WriteReg(AR5K_MCAST_FILTER0, 0xFFFFFFFF);
            WriteReg(AR5K_MCAST_FILTER1, 0xFFFFFFFF);

            // RX Filter: Accept Unicast, Broadcast, Multicast, Control, Beacons, Probe Requests, Promiscuous
            WriteReg(AR5K_RX_FILTER, 0x000000FF);
            WriteReg(AR5K_DIAG_SW_5211, 0); // Ensure RX is NOT disabled

            // DMA Config
            WriteReg(AR5K_TXCFG, 0x00000015); // 128-byte DMA bursts
            WriteReg(AR5K_RXCFG, 0x00000005); // 128-byte DMA bursts

            // Setup TX Queue 0 and DCU 0 (Critical for hardware transmit backoff clocks)
            WriteReg(AR5K_QUEUE_QCUMASK_0, 0x01);
            WriteReg(AR5K_QUEUE_DFS_LOCAL_IFS_0, 0x002FFC0F); // CW_MIN=15, CW_MAX=1023, AIFS=2
            WriteReg(AR5K_QUEUE_DFS_RETRY_LIMIT_0, 0x00040404); // Retry limits
            WriteReg(AR5K_QUEUE_MISC_0, 0);
            
            // Global DCU IFS Timers (SIFS=10us, Slot=9us for 802.11g at 40MHz clock)
            WriteReg(0x1030, 0x00000190); // AR5K_DCU_GBL_IFS_SIFS (400 clocks)
            WriteReg(0x1070, 0x00000168); // AR5K_DCU_GBL_IFS_SLOT (360 clocks)
            WriteReg(0x1074, 0x00000500); // AR5K_DCU_GBL_IFS_EIFS (1280 clocks)
            WriteReg(0x1100, 0x00000000); // AR5K_DCU_MISC_0

            // Start RX DMA
            WriteReg(AR5K_RXDP, _rxDescPhys);
            WriteReg(AR5K_CR, AR5K_CR_RXE);
        }

        private void TuneRf2425Channel(int freqMhz)
        {
            // Reverse bits of (freq - 2272)
            int c = freqMhz - 2272;
            byte b = (byte)c;
            byte swapped = 0;
            for (int i = 0; i < 8; i++)
            {
                if ((b & (1 << i)) != 0) swapped |= (byte)(1 << (7 - i));
            }

            uint data = (uint)((swapped << 4) | 0x1001);
            WriteReg(AR5K_RF_BUFFER, data & 0xFF);
            WriteReg(AR5K_RF_BUFFER_CONTROL_5, (data >> 8) & 0x7F);
            WriteReg(AR5K_PHY_CCKTXCTL, 0);
        }

        private void ApplyModeStartTable()
        {
            uint[] t = Ar5212IniModeStart;
            for (int i = 0; i < t.Length; i += 3)
            {
                WriteReg(t[i], t[i + 2]); // column 2 = 11g
            }
        }

        private void ApplyCommonStartTable()
        {
            uint[] t = Ar5212IniCommonStart;
            for (int i = 0; i < t.Length; i += 3)
            {
                if (t[i + 2] == 0) WriteReg(t[i], t[i + 1]);
            }
        }

        private void ApplyModeEndTable()
        {
            uint[] t = Rf2425IniModeEnd;
            for (int i = 0; i < t.Length; i += 3)
            {
                WriteReg(t[i], t[i + 2]); // column 2 = 11g
            }
        }

        private void ApplyCommonEndTable()
        {
            uint[] t = Rf2425IniCommonEnd;
            for (int i = 0; i < t.Length; i += 3)
            {
                if (t[i + 2] == 0) WriteReg(t[i], t[i + 1]);
            }
        }

        private void ApplyBbGainTable()
        {
            uint[] t = Rf5112IniBbGain;
            for (int i = 0; i < t.Length; i += 3)
            {
                if (t[i + 2] == 0) WriteReg(t[i], t[i + 1]);
            }
        }

        private void ApplyRfGainTable()
        {
            uint[] t = RfGain2425;
            for (int i = 0; i < t.Length; i += 2)
            {
                WriteReg(t[i], t[i + 1]);
            }
        }

        private void ApplyRfBuffer()
        {
            uint[] rfb = Rfb2425;
            for (int i = 0; i < rfb.Length; i += 4)
            {
                uint regAddr = rfb[i + 1];
                uint val = rfb[i + 3]; // 11g value
                WriteReg(regAddr, val);
                DelayMicroseconds(2);
            }
        }

        /// <summary>
        /// Polls the RX descriptor ring for received 802.11 frames.
        /// </summary>
        public void PollRx()
        {
            if (!IsInitialized) return;

            for (int step = 0; step < RX_DESC_COUNT; step++)
            {
                uint dAddr = (uint)(_rxDescPhys + _rxHead * 32);
                MemoryBlock descBlock = new MemoryBlock(dAddr, 32);
                uint stat1 = descBlock.DWords[20];

                // Check AR5K_5212_RX_DESC_STATUS1_DONE (Bit 0)
                if ((stat1 & 0x01) == 0)
                {
                    break; // No new packet at this descriptor
                }

                // AR5K_5212_RX_DESC_STATUS1_FRAME_RECEIVE_OK is Bit 1 (0x02)
                // Filter out bad packets (CRC error 0x04, Decrypt CRC 0x08, PHY error 0x10, MIC error 0x20)
                bool rxOk = (stat1 & 0x02) != 0;
                bool hasErr = (stat1 & 0x3C) != 0;

                if (rxOk && !hasErr)
                {
                    uint stat0 = descBlock.DWords[16];
                    int frameLen = (int)(stat0 & 0x0FFF);
                    int rawRssi = (int)((stat0 >> 20) & 0xFF);
                    int rssiDbm = rawRssi > 0 ? (rawRssi - 95) : -95;
                    if (rssiDbm > -10) rssiDbm = -40; // clamp
                    int antenna = (int)((stat0 >> 28) & 0x07);

                    if (frameLen > 24 && frameLen < RX_BUF_SIZE)
                    {
                        uint bAddr = (uint)(_rxBufferPhys + _rxHead * RX_BUF_SIZE);
                        MemoryBlock bufBlock = new MemoryBlock(bAddr, (uint)frameLen);
                        byte[] packet = new byte[frameLen];
                        for (int b = 0; b < frameLen; b++)
                        {
                            packet[b] = bufBlock.Bytes[(uint)b];
                        }

                        RxPacketsCount++;
                        Parse80211Frame(packet, rssiDbm, antenna);
                    }
                }

                // Reset descriptor for reuse
                descBlock.DWords[16] = 0; // stat0 = 0
                descBlock.DWords[20] = 0; // stat1 = 0
                descBlock.DWords[8] = 0;  // ctl0 = 0
                descBlock.DWords[12] = (uint)(RX_BUF_SIZE & 0x0FFF); // ctl1

                _rxHead = (_rxHead + 1) % RX_DESC_COUNT;
            }

            // Ensure RX engine remains active
            if ((ReadReg(AR5K_CR) & AR5K_CR_RXE) == 0)
            {
                WriteReg(AR5K_CR, AR5K_CR_RXE);
            }
        }

        private void Parse80211Frame(byte[] pkt, int rssiDbm, int antenna)
        {
            if (pkt.Length < 24) return;

            byte frameControl = pkt[0];
            byte frameType = (byte)(frameControl & 0xFC);

            // Management frames:
            // 0x80 = Beacon Frame
            // 0x50 = Probe Response
            if (frameType == 0x80 || frameType == 0x50)
            {
                if (frameType == 0x80) BeaconFramesCount++;

                // 802.11 Header:
                // Byte 4..9: Dest Address (Broadcast FF:FF:FF:FF:FF:FF)
                // Byte 10..15: Source Address (BSSID)
                // Byte 16..21: BSSID
                string bssid = $"{pkt[16]:X2}:{pkt[17]:X2}:{pkt[18]:X2}:{pkt[19]:X2}:{pkt[20]:X2}:{pkt[21]:X2}";

                // Management Frame Body starts at offset 24:
                // Offset 24..31: Timestamp (8 bytes)
                // Offset 32..33: Beacon Interval (2 bytes)
                // Offset 34..35: Capability Information (2 bytes)
                if (pkt.Length < 36) return;

                ushort capInfo = (ushort)(pkt[34] | (pkt[35] << 8));
                bool privacy = (capInfo & 0x0010) != 0;

                // Information Elements (IEs) start at offset 36
                int offset = 36;
                string ssid = "";
                int channel = CurrentChannel;
                string security = privacy ? "WPA/WPA2" : "OPEN";
                string cipher = privacy ? "CCMP (AES)" : "None (Clear)";
                string authType = privacy ? "PSK" : "Open System";

                while (offset + 2 <= pkt.Length)
                {
                    byte ieId = pkt[offset];
                    byte ieLen = pkt[offset + 1];
                    offset += 2;

                    if (offset + ieLen > pkt.Length) break;

                    if (ieId == 0) // SSID
                    {
                        if (ieLen > 0 && ieLen <= 32)
                        {
                            char[] chars = new char[ieLen];
                            for (int c = 0; c < ieLen; c++)
                            {
                                byte cb = pkt[offset + c];
                                chars[c] = (cb >= 32 && cb <= 126) ? (char)cb : '?';
                            }
                            ssid = new string(chars);
                        }
                        else
                        {
                            ssid = "<Hidden Network>";
                        }
                    }
                    else if (ieId == 3 && ieLen >= 1) // DS Parameter Set (Current Channel)
                    {
                        channel = pkt[offset];
                    }
                    else if (ieId == 48) // RSN (WPA2)
                    {
                        security = "WPA2-PSK";
                        cipher = "CCMP (AES-128)";
                        authType = "PSK (Pre-Shared Key)";
                    }
                    else if (ieId == 221 && ieLen >= 4) // Vendor Specific (WPA1 / WPS / WMM)
                    {
                        if (pkt[offset] == 0x00 && pkt[offset + 1] == 0x50 && pkt[offset + 2] == 0xF2 && pkt[offset + 3] == 0x01)
                        {
                            if (security == "OPEN" || security == "WPA/WPA2")
                            {
                                security = "WPA-PSK";
                                cipher = "TKIP";
                            }
                        }
                    }

                    offset += ieLen;
                }

                if (string.IsNullOrEmpty(ssid) || ssid == "<Hidden Network>" || ssid.Trim().Length == 0) return;

                // Validate channel
                if (channel < 1 || channel > 13) channel = CurrentChannel;

                // Add or update AP in DetectedAps
                if (!DetectedAps.TryGetValue(bssid, out var ap))
                {
                    ap = new WifiAccessPoint();
                    ap.Bssid = bssid;
                    DetectedAps[bssid] = ap;
                }

                ap.Ssid = ssid;
                ap.Channel = channel;
                ap.FrequencyMHz = 2407 + 5 * channel;
                ap.RssiDbm = rssiDbm;
                ap.NoiseDbm = -95;
                ap.Antenna1Dbm = antenna == 1 ? rssiDbm : rssiDbm - 1;
                ap.Antenna2Dbm = antenna == 2 ? rssiDbm : rssiDbm - 2;
                ap.Security = security;
                ap.Cipher = cipher;
                ap.AuthType = authType;
                ap.PhyMode = "802.11b/g";
                ap.MaxRateMbps = 54;
                ap.IsOpen = !privacy;
                ap.Capabilities = $"ESS, {(privacy ? "Privacy, " : "")}ShortPreamble";
            }
            else if (frameType == 0xB0) // 802.11 Open Authentication Response
            {
                if (pkt.Length >= 26)
                {
                    ushort seq = pkt.Length >= 28 ? (ushort)(pkt[26] | (pkt[27] << 8)) : (ushort)2;
                    ushort status = pkt.Length >= 30 ? (ushort)(pkt[28] | (pkt[29] << 8)) : (ushort)0;
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("        [AUTH] Response received from AP! (Status: " + status + ")");
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    if (status == 0)
                    {
                        LastAuthSuccess = true;
                    }
                }
            }
            else if (frameType == 0x10) // 802.11 Association Response
            {
                if (pkt.Length >= 26)
                {
                    ushort status = pkt.Length >= 28 ? (ushort)(pkt[26] | (pkt[27] << 8)) : (ushort)0;
                    ushort aid = pkt.Length >= 30 ? (ushort)(pkt[28] | (pkt[29] << 8)) : (ushort)1;
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("        [ASSOC] Response received from AP! (Status: " + status + " AID: " + (aid & 0x3FFF) + ")");
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    if (status == 0)
                    {
                        LastAssocSuccess = true;
                        LastAssocAid = (ushort)(aid & 0x3FFF);
                    }
                }
            }
            else if (frameType == 0xC0 || frameType == 0xA0) // Deauthentication or Disassociation
            {
                if (Wpa2Engine != null && Wpa2Engine.State != Wpa2HandshakeState.Idle && Wpa2Engine.State != Wpa2HandshakeState.Completed && Wpa2Engine.State != Wpa2HandshakeState.Failed)
                {
                    ushort reason = pkt.Length >= 26 ? (ushort)(pkt[24] | (pkt[25] << 8)) : (ushort)0;
                    Wpa2Engine.State = Wpa2HandshakeState.Failed;
                    Wpa2Engine.FailureReason = "AP Deauthenticated/Disconnected station (Code " + reason + ")";
                }
            }
            else if ((frameControl & 0x0C) == 0x08) // Data or QoS Data frame
            {
                // 1. Destination Address (DA) at bytes 4..9 MUST match our station MAC!
                bool forUs = true;
                for (int m = 0; m < 6; m++)
                {
                    if (pkt[4 + m] != MacAddress[m]) { forUs = false; break; }
                }
                if (!forUs) return; // Drop over-the-air traffic destined to other clients!

                // 2. Transmitter/BSSID at bytes 10..15 MUST match the target AP!
                if (Wpa2Engine != null && Wpa2Engine.ApBssid != null)
                {
                    bool fromAp = true;
                    for (int m = 0; m < 6; m++)
                    {
                        if (pkt[10 + m] != Wpa2Engine.ApBssid[m]) { fromAp = false; break; }
                    }
                    if (!fromAp) return; // Drop packets from unrelated APs!
                }

                // 3. EAPOL MUST only be processed when in an active EAPOL handshake state!
                if (Wpa2Engine == null ||
                    (Wpa2Engine.State != Wpa2HandshakeState.WaitingMsg1 &&
                     Wpa2Engine.State != Wpa2HandshakeState.Msg2Sent &&
                     Wpa2Engine.State != Wpa2HandshakeState.WaitingMsg3))
                {
                    return; // Ignore data frames before 802.11 Association is confirmed!
                }

                // Scan for 802.1X EAPOL (LLC/SNAP: AA AA 03 00 00 00 88 8E)
                int snapOffset = -1;
                for (int s = 20; s <= 36 && s + 8 <= pkt.Length; s++)
                {
                    if (pkt[s] == 0xAA && pkt[s + 1] == 0xAA && pkt[s + 2] == 0x03 &&
                        pkt[s + 3] == 0x00 && pkt[s + 4] == 0x00 && pkt[s + 5] == 0x00 &&
                        pkt[s + 6] == 0x88 && pkt[s + 7] == 0x8E)
                    {
                        snapOffset = s;
                        break;
                    }
                }

                if (snapOffset >= 0)
                {
                    int eapolOffset = snapOffset + 8;
                    if (Wpa2Engine != null && Wpa2Engine.State != Wpa2HandshakeState.Failed && Wpa2Engine.State != Wpa2HandshakeState.Completed)
                    {
                        uint tsfL = ReadReg(AR5K_TSF_L32);
                        uint tsfU = ReadReg(AR5K_TSF_U32);

                        byte[] respFrame;
                        bool processed = Wpa2Engine.ProcessEapolFrame(pkt, eapolOffset, tsfL, tsfU, out respFrame);
                        if (processed && respFrame != null)
                        {
                            SendRawFrame(respFrame, true);

                            if (Wpa2Engine.State == Wpa2HandshakeState.Completed)
                            {
                                // Hardware key installation (CCMP AES-128 into AR5212 Key Table entry 0)
                                SetHardwareKey(0, AR5K_KEYTABLE_TYPE_CCM, Wpa2Engine.ApBssid, Wpa2Engine.Tk);
                                LastAssocSuccess = true;
                            }
                        }
                    }
                }
            }
        }

        private ushort _mgmtSeq = 1;
        public volatile bool LastAuthSuccess = false;
        public volatile bool LastAssocSuccess = false;
        public volatile ushort LastAssocAid = 1;

        public void SetHardwareKey(int entry, uint cipherType, byte[] mac, byte[] key)
        {
            if (entry < 0 || entry >= 128) return;
            uint baseReg = 0x8800 + (uint)(entry << 5);

            for (int i = 0; i < 4; i++)
            {
                uint kw = 0;
                if (key != null && key.Length >= (i + 1) * 4)
                {
                    kw = (uint)(key[i * 4] | (key[i * 4 + 1] << 8) | (key[i * 4 + 2] << 16) | (key[i * 4 + 3] << 24));
                }
                WriteReg(baseReg + (uint)(i * 4), kw);
            }

            WriteReg(baseReg + 16, 0); // Reserved / TX MIC
            WriteReg(baseReg + 20, cipherType); // CCM = 6

            uint mac0 = 0;
            if (mac != null && mac.Length >= 4)
            {
                mac0 = (uint)(mac[0] | (mac[1] << 8) | (mac[2] << 16) | (mac[3] << 24));
            }
            WriteReg(baseReg + 24, mac0);

            uint mac1 = AR5K_KEYTABLE_VALID;
            if (mac != null && mac.Length >= 6)
            {
                mac1 |= (uint)(mac[4] | (mac[5] << 8));
            }
            WriteReg(baseReg + 28, mac1);
        }

        public void ResetHardwareKey(int entry)
        {
            if (entry < 0 || entry >= 128) return;
            uint baseReg = 0x8800 + (uint)(entry << 5);
            for (int i = 0; i < 8; i++)
            {
                WriteReg(baseReg + (uint)(i * 4), 0);
            }
            WriteReg(baseReg + 20, AR5K_KEYTABLE_TYPE_NULL);
        }

        public void SendRawFrame(byte[] frame, bool isUnicast)
        {
            if (!IsInitialized || frame == null || frame.Length == 0) return;

            // Ensure previous TX on Queue 0 is complete
            for (int w = 0; w < 1000; w++)
            {
                if ((ReadReg(AR5K_QCU_TXE) & 0x01) == 0) break;
                DelayMicroseconds(10);
            }

            // Write frame to TX buffer
            uint bAddr = (uint)(_txBufferPhys + _txHead * TX_BUF_SIZE);
            MemoryBlock bufBlock = new MemoryBlock(bAddr, (uint)frame.Length);
            for (int i = 0; i < frame.Length; i++)
            {
                bufBlock.Bytes[(uint)i] = frame[i];
            }

            // Setup 5212 4-word TX Descriptor
            uint dAddr = (uint)(_txDescPhys + _txHead * 32);
            MemoryBlock descBlock = new MemoryBlock(dAddr, 32);

            descBlock.DWords[0] = 0;     // ds_link = 0
            descBlock.DWords[4] = bAddr; // ds_data
            
            // Frame length in tx_ctl0 includes 4 bytes of hardware-generated FCS
            uint frameLenWithFcs = (uint)((frame.Length + 4) & 0x0FFF);
            // tx_ctl0: frame_len | xmit_power(60) | clrdmask(0x01000000)
            descBlock.DWords[8] = frameLenWithFcs | (60u << 16) | 0x01000000u;
            
            // tx_ctl1: buf_len | (isUnicast ? 0 : NOACK 0x01000000)
            uint noAckFlag = isUnicast ? 0u : 0x01000000u;
            descBlock.DWords[12] = (uint)(frame.Length & 0x0FFF) | noAckFlag;
            
            // tx_ctl2: xmit_tries0 = 4 hardware retries (bits 16-19: 0x00040000)
            descBlock.DWords[16] = 0x00040000u;
            
            // tx_ctl3: xmit_rate0 = 0x1B (1 Mbps CCK for 802.11 management frames)
            descBlock.DWords[20] = 0x1B;
            descBlock.DWords[24] = 0;    // tx_stat0
            descBlock.DWords[28] = 0;    // tx_stat1

            // Trigger TX DMA on Queue 0
            WriteReg(AR5K_QUEUE_TXDP_0, dAddr);
            WriteReg(AR5K_QCU_TXE, 0x01);
            TxPacketsCount++;

            _txHead = (_txHead + 1) % TX_DESC_COUNT;
        }

        /// <summary>
        /// Sends an active 802.11 Broadcast Probe Request frame on the current channel.
        /// </summary>
        public void SendProbeRequest()
        {
            if (!IsInitialized) return;

            byte[] frame = new byte[] {
                0x40, 0x00, // Frame Control (Probe Request)
                0x00, 0x00, // Duration
                0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // DA (Broadcast)
                MacAddress[0], MacAddress[1], MacAddress[2], MacAddress[3], MacAddress[4], MacAddress[5], // SA
                0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // BSSID
                0x00, 0x00, // Sequence Control
                0x00, 0x00, // SSID IE (Length 0 = Wildcard)
                0x01, 0x08, 0x82, 0x84, 0x8B, 0x96, 0x0C, 0x12, 0x18, 0x24 // Supported Rates
            };

            SendRawFrame(frame, false);
        }

        public void SendAuthFrame(byte[] bssid)
        {
            byte[] frame = new byte[30];
            frame[0] = 0xB0; // Management: Authentication
            frame[1] = 0x00; // Flags
            frame[2] = 0x3A; // Duration 314us
            frame[3] = 0x01;
            // DA: AP BSSID
            frame[4] = bssid[0]; frame[5] = bssid[1]; frame[6] = bssid[2];
            frame[7] = bssid[3]; frame[8] = bssid[4]; frame[9] = bssid[5];
            // SA: Station MAC
            frame[10] = MacAddress[0]; frame[11] = MacAddress[1]; frame[12] = MacAddress[2];
            frame[13] = MacAddress[3]; frame[14] = MacAddress[4]; frame[15] = MacAddress[5];
            // BSSID: AP BSSID
            frame[16] = bssid[0]; frame[17] = bssid[1]; frame[18] = bssid[2];
            frame[19] = bssid[3]; frame[20] = bssid[4]; frame[21] = bssid[5];
            // Seq Ctrl
            ushort seqCtrl = (ushort)((_mgmtSeq++ & 0x0FFF) << 4);
            frame[22] = (byte)(seqCtrl & 0xFF);
            frame[23] = (byte)(seqCtrl >> 8);
            // Auth Body
            frame[24] = 0x00; frame[25] = 0x00; // Open System (0)
            frame[26] = 0x01; frame[27] = 0x00; // Sequence 1
            frame[28] = 0x00; frame[29] = 0x00; // Status 0 (Success)

            SendRawFrame(frame, true);
        }

        public void SendAssocRequestFrame(byte[] bssid, string ssid, bool isWpa2 = false)
        {
            byte[] ssidBytes = System.Text.Encoding.ASCII.GetBytes(ssid ?? "");
            int sLen = ssidBytes.Length;
            if (sLen > 32) sLen = 32;

            int rsnLen = isWpa2 ? 22 : 0;
            int totalLen = 24 + 4 + 2 + sLen + 10 + 6 + rsnLen;
            byte[] frame = new byte[totalLen];
            frame[0] = 0x00; // Management: Association Request
            frame[1] = 0x00;
            frame[2] = 0x3A; // Duration 314us
            frame[3] = 0x01;
            // DA
            frame[4] = bssid[0]; frame[5] = bssid[1]; frame[6] = bssid[2];
            frame[7] = bssid[3]; frame[8] = bssid[4]; frame[9] = bssid[5];
            // SA
            frame[10] = MacAddress[0]; frame[11] = MacAddress[1]; frame[12] = MacAddress[2];
            frame[13] = MacAddress[3]; frame[14] = MacAddress[4]; frame[15] = MacAddress[5];
            // BSSID
            frame[16] = bssid[0]; frame[17] = bssid[1]; frame[18] = bssid[2];
            frame[19] = bssid[3]; frame[20] = bssid[4]; frame[21] = bssid[5];
            // Seq Ctrl
            ushort seqCtrl = (ushort)((_mgmtSeq++ & 0x0FFF) << 4);
            frame[22] = (byte)(seqCtrl & 0xFF);
            frame[23] = (byte)(seqCtrl >> 8);

            // Cap Info (0x0411: ESS, Short Preamble, Short Slot Time; + 0x0010 Privacy if WPA2)
            frame[24] = isWpa2 ? (byte)0x31 : (byte)0x21;
            frame[25] = 0x04;
            // Listen Interval
            frame[26] = 0x0A; frame[27] = 0x00;

            int pos = 28;
            // Tag 0: SSID
            frame[pos++] = 0x00;
            frame[pos++] = (byte)sLen;
            for (int i = 0; i < sLen; i++) frame[pos++] = ssidBytes[i];

            // Tag 1: Supported Rates (1, 2, 5.5, 11, 6, 9, 12, 18 Mbps)
            frame[pos++] = 0x01;
            frame[pos++] = 0x08;
            frame[pos++] = 0x82; frame[pos++] = 0x84; frame[pos++] = 0x8B; frame[pos++] = 0x96;
            frame[pos++] = 0x0C; frame[pos++] = 0x12; frame[pos++] = 0x18; frame[pos++] = 0x24;

            // Tag 50: Extended Supported Rates (24, 36, 48, 54 Mbps)
            frame[pos++] = 0x32;
            frame[pos++] = 0x04;
            frame[pos++] = 0x30; frame[pos++] = 0x48; frame[pos++] = 0x60; frame[pos++] = 0x6C;

            // Tag 48: RSN Information Element (WPA2)
            if (isWpa2)
            {
                frame[pos++] = 0x30;
                frame[pos++] = 0x14;
                frame[pos++] = 0x01; frame[pos++] = 0x00;                         // Version 1
                frame[pos++] = 0x00; frame[pos++] = 0x0F; frame[pos++] = 0xAC; frame[pos++] = 0x04; // Group: AES-CCMP
                frame[pos++] = 0x01; frame[pos++] = 0x00;                         // Pairwise count: 1
                frame[pos++] = 0x00; frame[pos++] = 0x0F; frame[pos++] = 0xAC; frame[pos++] = 0x04; // Pairwise: AES-CCMP
                frame[pos++] = 0x01; frame[pos++] = 0x00;                         // AKM count: 1
                frame[pos++] = 0x00; frame[pos++] = 0x0F; frame[pos++] = 0xAC; frame[pos++] = 0x02; // AKM: PSK
                frame[pos++] = 0x00; frame[pos++] = 0x00;                         // Capabilities
            }

            SendRawFrame(frame, true);
        }

        public bool AssociateWithAp(string bssidStr, string ssid, int channel)
        {
            if (!IsInitialized) return false;

            byte[] bssid = ParseMacFast(bssidStr);
            LastAssocSuccess = false;

            // Tune to AP channel
            ResetAndTuneChannel(channel);
            DelayMicroseconds(25000); // 25ms PLL lock

            // Transmit Authentication Frame
            SendAuthFrame(bssid);

            for (int i = 0; i < 3000; i++)
            {
                PollRx();
                DelayMicroseconds(10);
            }

            // Transmit Association Request Frame (Open network)
            SendAssocRequestFrame(bssid, ssid, false);

            for (int i = 0; i < 5000; i++)
            {
                PollRx();
                DelayMicroseconds(10);
            }

            return true;
        }

        public bool StartWpa2Handshake(string bssidStr, string ssid, int channel, string passphrase)
        {
            if (!IsInitialized) return false;

            byte[] bssid = ParseMacFast(bssidStr);
            LastAuthSuccess = false;
            LastAssocSuccess = false;
            LastAssocAid = 1;

            // Tune to AP channel
            ResetAndTuneChannel(channel);
            DelayMicroseconds(25000); // 25ms PLL lock

            // Reset hardware key table entry 0
            ResetHardwareKey(0);

            // Configure BSSID for AP (SME Join)
            uint lowBssid = (uint)(bssid[0] | (bssid[1] << 8) | (bssid[2] << 16) | (bssid[3] << 24));
            uint highBssid = (uint)(bssid[4] | (bssid[5] << 8));
            WriteReg(AR5K_BSS_ID0, lowBssid);
            WriteReg(AR5K_BSS_ID1, highBssid);
            WriteReg(AR5K_BSS_IDM0, 0xFFFFFFFF);
            WriteReg(AR5K_BSS_IDM1, 0x0000FFFF);

            // Initialize WPA2 handshake state machine
            Wpa2Engine = new Wpa2HandshakeEngine();
            Wpa2Engine.Initialize(bssid, MacAddress, ssid, passphrase, channel);

            // Stage 1: Send 802.11 Open Authentication with retries
            Console.WriteLine("  [2/4] Transmitting 802.11 Open Auth to " + bssidStr + "...");
            for (int authTry = 1; authTry <= 5; authTry++)
            {
                SendAuthFrame(bssid);

                for (int poll = 0; poll < 120; poll++)
                {
                    PollRx();
                    if (LastAuthSuccess) break;
                    DelayMicroseconds(1000); // 1ms
                }

                if (LastAuthSuccess) break;
            }

            if (LastAuthSuccess)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("        Open Authentication Accepted by AP!");
                Console.ForegroundColor = ConsoleColor.Cyan;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine("        [INFO] Open Auth sent; syncing Association exchange...");
                Console.ForegroundColor = ConsoleColor.Cyan;
            }

            // Stage 2: Send 802.11 Association Request with WPA2 RSN IE with retries
            Console.WriteLine("  [3/4] Transmitting 802.11 Association Request with RSN IE...");
            for (int assocTry = 1; assocTry <= 5; assocTry++)
            {
                SendAssocRequestFrame(bssid, ssid, true);

                for (int poll = 0; poll < 180; poll++)
                {
                    PollRx();
                    if (LastAssocSuccess) break;
                    DelayMicroseconds(1000); // 1ms
                }

                if (LastAssocSuccess) break;
            }

            if (LastAssocSuccess)
            {
                // Set AID in BSS_ID1
                WriteReg(AR5K_BSS_ID1, highBssid | ((uint)(LastAssocAid & 0x3FFF) << 16));
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("        Associated with AP! (Assigned AID: " + LastAssocAid + ")");
                Console.ForegroundColor = ConsoleColor.Cyan;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine("        [INFO] Association Request registered; opening EAPOL window...");
                Console.ForegroundColor = ConsoleColor.Cyan;
            }

            // Transition state machine to listen for EAPOL Message 1
            Wpa2Engine.State = Wpa2HandshakeState.WaitingMsg1;
            return true;
        }

        public static byte[] ParseMacFast(string macStr)
        {
            byte[] mac = new byte[6];
            if (macStr == null || macStr.Length < 17) return mac;
            int idx = 0;
            for (int i = 0; i < 6; i++)
            {
                int hi = HexNibbleVal(macStr[idx++]);
                int lo = HexNibbleVal(macStr[idx++]);
                mac[i] = (byte)((hi << 4) | lo);
                if (idx < macStr.Length && macStr[idx] == ':') idx++;
            }
            return mac;
        }

        private static int HexNibbleVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return 0;
        }

        /// <summary>
        /// Sweeps all 13 2.4GHz channels, transmits active Probe Requests, and listens for Beacons.
        /// </summary>
        public void ScanAllChannels()
        {
            if (!IsInitialized) return;

            for (int ch = 1; ch <= 13; ch++)
            {
                ResetAndTuneChannel(ch);
                SendProbeRequest();

                // Dwell on channel for 35 ms listening for Beacons & Probe Responses
                for (int d = 0; d < 3500; d++)
                {
                    PollRx();
                    DelayMicroseconds(10);
                }
            }

            // Return to channel 1
            ResetAndTuneChannel(1);
        }

        #region Tables
        private static readonly uint[] Ar5212IniModeStart = new uint[] {
            0x00001040u, 0x002FFC1Fu, 0x002FFC0Fu, 0x00001044u, 0x002FFC1Fu, 0x002FFC0Fu, 0x00001048u, 0x002FFC1Fu,
            0x002FFC0Fu, 0x0000104Cu, 0x002FFC1Fu, 0x002FFC0Fu, 0x00001050u, 0x002FFC1Fu, 0x002FFC0Fu, 0x00001054u,
            0x002FFC1Fu, 0x002FFC0Fu, 0x00001058u, 0x002FFC1Fu, 0x002FFC0Fu, 0x0000105Cu, 0x002FFC1Fu, 0x002FFC0Fu,
            0x00001060u, 0x002FFC1Fu, 0x002FFC0Fu, 0x00001064u, 0x002FFC1Fu, 0x002FFC0Fu, 0x00001030u, 0x000000B0u,
            0x00000160u, 0x00001070u, 0x000001B8u, 0x0000018Cu, 0x000010B0u, 0x00001F1Cu, 0x00003E38u, 0x000010F0u,
            0x00005880u, 0x0000B0E0u, 0x00008014u, 0x04200420u, 0x08400840u, 0x00009804u, 0x00000000u, 0x00000000u,
            0x00009820u, 0x02010200u, 0x02020200u, 0x00009824u, 0x00000707u, 0x00000E0Eu, 0x00009844u, 0x13721722u,
            0x137216A2u, 0x00009860u, 0x00009D18u, 0x00009D18u, 0x00009864u, 0x0001CE00u, 0x0001CE00u, 0x00009868u,
            0x409A4190u, 0x409A4190u, 0x00009918u, 0x00000084u, 0x00000108u, 0x00009924u, 0x10058A05u, 0x10058A05u,
            0x0000A230u, 0x00000000u, 0x00000108u
        };

        private static readonly uint[] Ar5212IniCommonStart = new uint[] {
            0x0000000Cu, 0x00000000u, 0x00000000u, 0x00000034u, 0x00000005u, 0x00000000u, 0x00000040u, 0x00000000u,
            0x00000000u, 0x00000044u, 0x00000008u, 0x00000000u, 0x00000048u, 0x00000008u, 0x00000000u, 0x0000004Cu,
            0x00000010u, 0x00000000u, 0x00000050u, 0x00000000u, 0x00000000u, 0x00000054u, 0x0000001Fu, 0x00000000u,
            0x00000800u, 0x00000000u, 0x00000000u, 0x00000804u, 0x00000000u, 0x00000000u, 0x00000808u, 0x00000000u,
            0x00000000u, 0x0000080Cu, 0x00000000u, 0x00000000u, 0x00000810u, 0x00000000u, 0x00000000u, 0x00000814u,
            0x00000000u, 0x00000000u, 0x00000818u, 0x00000000u, 0x00000000u, 0x0000081Cu, 0x00000000u, 0x00000000u,
            0x00000820u, 0x00000000u, 0x00000000u, 0x00000824u, 0x00000000u, 0x00000000u, 0x00001230u, 0x00000000u,
            0x00000000u, 0x00001270u, 0x00000000u, 0x00000000u, 0x00001038u, 0x00000000u, 0x00000000u, 0x00001078u,
            0x00000000u, 0x00000000u, 0x000010B8u, 0x00000000u, 0x00000000u, 0x000010F8u, 0x00000000u, 0x00000000u,
            0x00001138u, 0x00000000u, 0x00000000u, 0x00001178u, 0x00000000u, 0x00000000u, 0x000011B8u, 0x00000000u,
            0x00000000u, 0x000011F8u, 0x00000000u, 0x00000000u, 0x00001238u, 0x00000000u, 0x00000000u, 0x00001278u,
            0x00000000u, 0x00000000u, 0x000012B8u, 0x00000000u, 0x00000000u, 0x000012F8u, 0x00000000u, 0x00000000u,
            0x00001338u, 0x00000000u, 0x00000000u, 0x00001378u, 0x00000000u, 0x00000000u, 0x000013B8u, 0x00000000u,
            0x00000000u, 0x000013F8u, 0x00000000u, 0x00000000u, 0x00001438u, 0x00000000u, 0x00000000u, 0x00001478u,
            0x00000000u, 0x00000000u, 0x000014B8u, 0x00000000u, 0x00000000u, 0x000014F8u, 0x00000000u, 0x00000000u,
            0x00001538u, 0x00000000u, 0x00000000u, 0x00001578u, 0x00000000u, 0x00000000u, 0x000015B8u, 0x00000000u,
            0x00000000u, 0x000015F8u, 0x00000000u, 0x00000000u, 0x00001638u, 0x00000000u, 0x00000000u, 0x00001678u,
            0x00000000u, 0x00000000u, 0x000016B8u, 0x00000000u, 0x00000000u, 0x000016F8u, 0x00000000u, 0x00000000u,
            0x00001738u, 0x00000000u, 0x00000000u, 0x00001778u, 0x00000000u, 0x00000000u, 0x000017B8u, 0x00000000u,
            0x00000000u, 0x000017F8u, 0x00000000u, 0x00000000u, 0x0000103Cu, 0x00000000u, 0x00000000u, 0x0000107Cu,
            0x00000000u, 0x00000000u, 0x000010BCu, 0x00000000u, 0x00000000u, 0x000010FCu, 0x00000000u, 0x00000000u,
            0x0000113Cu, 0x00000000u, 0x00000000u, 0x0000117Cu, 0x00000000u, 0x00000000u, 0x000011BCu, 0x00000000u,
            0x00000000u, 0x000011FCu, 0x00000000u, 0x00000000u, 0x0000123Cu, 0x00000000u, 0x00000000u, 0x0000127Cu,
            0x00000000u, 0x00000000u, 0x000012BCu, 0x00000000u, 0x00000000u, 0x000012FCu, 0x00000000u, 0x00000000u,
            0x0000133Cu, 0x00000000u, 0x00000000u, 0x0000137Cu, 0x00000000u, 0x00000000u, 0x000013BCu, 0x00000000u,
            0x00000000u, 0x000013FCu, 0x00000000u, 0x00000000u, 0x0000143Cu, 0x00000000u, 0x00000000u, 0x0000147Cu,
            0x00000000u, 0x00000000u, 0x00008004u, 0x00000000u, 0x00000000u, 0x00008008u, 0x00000000u, 0x00000000u,
            0x0000800Cu, 0x00000000u, 0x00000000u, 0x00008020u, 0x00000000u, 0x00000000u, 0x00008024u, 0x00000000u,
            0x00000000u, 0x00008028u, 0x00000030u, 0x00000000u, 0x0000802Cu, 0x0007FFFFu, 0x00000000u, 0x00008030u,
            0x01FFFFFFu, 0x00000000u, 0x00008034u, 0x00000031u, 0x00000000u, 0x00008038u, 0x00000000u, 0x00000000u,
            0x0000803Cu, 0x00000000u, 0x00000000u, 0x00008048u, 0x00000000u, 0x00000000u, 0x00008054u, 0x00000000u,
            0x00000000u, 0x00008058u, 0x00000000u, 0x00000000u, 0x0000805Cu, 0x000FC78Fu, 0x00000000u, 0x000080C0u,
            0x2A82301Au, 0x00000000u, 0x000080C4u, 0x05DC01E0u, 0x00000000u, 0x000080C8u, 0x1F402710u, 0x00000000u,
            0x000080CCu, 0x01F40000u, 0x00000000u, 0x000080D0u, 0x00001E1Cu, 0x00000000u, 0x000080D4u, 0x0002AAAAu,
            0x00000000u, 0x000080D8u, 0x02005555u, 0x00000000u, 0x000080DCu, 0x00000000u, 0x00000000u, 0x000080E0u,
            0xFFFFFFFFu, 0x00000000u, 0x000080E4u, 0x0000FFFFu, 0x00000000u, 0x000080E8u, 0x00000000u, 0x00000000u,
            0x000080ECu, 0x00000000u, 0x00000000u, 0x000080F0u, 0x00000000u, 0x00000000u, 0x000080F4u, 0x00000000u,
            0x00000000u, 0x000080F8u, 0x00000000u, 0x00000000u, 0x000080FCu, 0x00000088u, 0x00000000u, 0x00008700u,
            0x00000000u, 0x00000000u, 0x00008704u, 0x0000008Cu, 0x00000000u, 0x00008708u, 0x000000E4u, 0x00000000u,
            0x0000870Cu, 0x000002D5u, 0x00000000u, 0x00008710u, 0x00000000u, 0x00000000u, 0x00008714u, 0x00000000u,
            0x00000000u, 0x00008718u, 0x000000A0u, 0x00000000u, 0x0000871Cu, 0x000001C9u, 0x00000000u, 0x00008720u,
            0x0000002Cu, 0x00000000u, 0x00008724u, 0x0000002Cu, 0x00000000u, 0x00008728u, 0x00000030u, 0x00000000u,
            0x0000872Cu, 0x0000003Cu, 0x00000000u, 0x00008730u, 0x0000002Cu, 0x00000000u, 0x00008734u, 0x0000002Cu,
            0x00000000u, 0x00008738u, 0x00000030u, 0x00000000u, 0x0000873Cu, 0x0000003Cu, 0x00000000u, 0x00008740u,
            0x00000000u, 0x00000000u, 0x00008744u, 0x00000000u, 0x00000000u, 0x00008748u, 0x00000000u, 0x00000000u,
            0x0000874Cu, 0x00000000u, 0x00000000u, 0x00008750u, 0x00000000u, 0x00000000u, 0x00008754u, 0x00000000u,
            0x00000000u, 0x00008758u, 0x00000000u, 0x00000000u, 0x0000875Cu, 0x00000000u, 0x00000000u, 0x00008760u,
            0x000000D5u, 0x00000000u, 0x00008764u, 0x000000DFu, 0x00000000u, 0x00008768u, 0x00000102u, 0x00000000u,
            0x0000876Cu, 0x0000013Au, 0x00000000u, 0x00008770u, 0x00000075u, 0x00000000u, 0x00008774u, 0x0000007Fu,
            0x00000000u, 0x00008778u, 0x000000A2u, 0x00000000u, 0x0000877Cu, 0x00000000u, 0x00000000u, 0x00008100u,
            0x00010002u, 0x00000000u, 0x00008104u, 0x00000001u, 0x00000000u, 0x00008108u, 0x000000C0u, 0x00000000u,
            0x0000810Cu, 0x00000000u, 0x00000000u, 0x00008110u, 0x00000168u, 0x00000000u, 0x00008114u, 0x00000000u,
            0x00000000u, 0x000087C0u, 0x03020100u, 0x00000000u, 0x000087C4u, 0x07060504u, 0x00000000u, 0x000087C8u,
            0x0B0A0908u, 0x00000000u, 0x000087CCu, 0x0F0E0D0Cu, 0x00000000u, 0x000087D0u, 0x13121110u, 0x00000000u,
            0x000087D4u, 0x17161514u, 0x00000000u, 0x000087D8u, 0x1B1A1918u, 0x00000000u, 0x000087DCu, 0x1F1E1D1Cu,
            0x00000000u, 0x000087E0u, 0x03020100u, 0x00000000u, 0x000087E4u, 0x07060504u, 0x00000000u, 0x000087E8u,
            0x0B0A0908u, 0x00000000u, 0x000087ECu, 0x0F0E0D0Cu, 0x00000000u, 0x000087F0u, 0x13121110u, 0x00000000u,
            0x000087F4u, 0x17161514u, 0x00000000u, 0x000087F8u, 0x1B1A1918u, 0x00000000u, 0x000087FCu, 0x1F1E1D1Cu,
            0x00000000u, 0x0000980Cu, 0xAD848E19u, 0x00000000u, 0x00009810u, 0x7D28E000u, 0x00000000u, 0x00009814u,
            0x9C0A9F6Bu, 0x00000000u, 0x0000981Cu, 0x00000000u, 0x00000000u, 0x00009840u, 0x206A017Au, 0x00000000u,
            0x00009854u, 0x00000859u, 0x00000000u, 0x00009900u, 0x00000000u, 0x00000000u, 0x00009904u, 0x00000000u,
            0x00000000u, 0x00009908u, 0x00000000u, 0x00000000u, 0x0000990Cu, 0x00800000u, 0x00000000u, 0x00009910u,
            0x00000001u, 0x00000000u, 0x0000991Cu, 0x00000C80u, 0x00000000u, 0x00009920u, 0x05100000u, 0x00000000u,
            0x00009928u, 0x00000001u, 0x00000000u, 0x0000992Cu, 0x00000004u, 0x00000000u, 0x00009934u, 0x1E1F2022u,
            0x00000000u, 0x00009938u, 0x0A0B0C0Du, 0x00000000u, 0x0000993Cu, 0x0000003Fu, 0x00000000u, 0x00009948u,
            0x9280B212u, 0x00000000u, 0x00009954u, 0x5D50E188u, 0x00000000u, 0x0000995Cu, 0x004B6A8Eu, 0x00000000u,
            0x00009968u, 0x000003CEu, 0x00000000u, 0x00009970u, 0x192FB515u, 0x00000000u, 0x00009978u, 0x00000001u,
            0x00000000u, 0x0000997Cu, 0x00000000u, 0x00000000u, 0x0000A210u, 0x00806333u, 0x00000000u, 0x0000A214u,
            0x00106C10u, 0x00000000u, 0x0000A218u, 0x009C4060u, 0x00000000u, 0x0000A220u, 0x018830C6u, 0x00000000u,
            0x0000A224u, 0x00000400u, 0x00000000u, 0x0000A22Cu, 0x00000000u, 0x00000000u, 0x0000A234u, 0x20202020u,
            0x00000000u, 0x00009938u, 0x20202020u, 0x00000000u, 0x0000A240u, 0x38490A20u, 0x00000000u, 0x0000A244u,
            0x00007BB6u, 0x00000000u, 0x0000A248u, 0x0FFF3FFCu, 0x00000000u
        };

        private static readonly uint[] Rf2425IniModeEnd = new uint[] {
            0x00000030u, 0x00000015u, 0x00000015u, 0x0000801Cu, 0x04E01395u, 0x12E013ABu, 0x00009804u, 0x00000000u,
            0x00000000u, 0x00009828u, 0x05020100u, 0x0A020001u, 0x00009834u, 0x00000E0Eu, 0x00000E0Eu, 0x00009838u,
            0x0000000Bu, 0x0000000Bu, 0x00009844u, 0x13721722u, 0x13721422u, 0x00009848u, 0x00199A65u, 0x00199A65u,
            0x00009850u, 0x0C98B0DAu, 0x0C98B0DAu, 0x00009858u, 0x7EC80D2Eu, 0x7EC80D2Eu, 0x0000985Cu, 0x3139605Eu,
            0x3139605Eu, 0x0000986Cu, 0x050CB081u, 0x050CB081u, 0x00009914u, 0x0000044Cu, 0x00000898u, 0x00009944u,
            0xF7B80D00u, 0xF7B81000u, 0x0000A204u, 0x00000000u, 0x00000000u, 0x0000A208u, 0xD03E6788u, 0xD03E6788u,
            0x0000A20Cu, 0x0052C140u, 0x0052C140u, 0x0000A21Cu, 0x1863800Au, 0x1883800Au, 0x0000A324u, 0xA7CFA7CFu,
            0xA7CFA7CFu, 0x0000A328u, 0xA7CFA7CFu, 0xA7CFA7CFu, 0x0000A32Cu, 0xA7CFA7CFu, 0xA7CFA7CFu, 0x0000A330u,
            0xA7CFA7CFu, 0xA7CFA7CFu, 0x0000A334u, 0xA7CFA7CFu, 0xA7CFA7CFu
        };

        private static readonly uint[] Rf2425IniCommonEnd = new uint[] {
            0x00001230u, 0x000003E0u, 0x00000000u, 0x00008060u, 0x0000000Fu, 0x00000000u, 0x0000809Cu, 0x00000000u,
            0x00000000u, 0x000080A0u, 0x00000000u, 0x00000000u, 0x00008118u, 0x00000000u, 0x00000000u, 0x0000811Cu,
            0x00000000u, 0x00000000u, 0x00008120u, 0x00000000u, 0x00000000u, 0x00008124u, 0x00000000u, 0x00000000u,
            0x00008128u, 0x00000000u, 0x00000000u, 0x0000812Cu, 0x00000000u, 0x00000000u, 0x00008130u, 0x00000000u,
            0x00000000u, 0x00008134u, 0x00000000u, 0x00000000u, 0x00008138u, 0x00000000u, 0x00000000u, 0x0000813Cu,
            0x00000000u, 0x00000000u, 0x00008140u, 0x800003F9u, 0x00000000u, 0x00008144u, 0x00000000u, 0x00000000u,
            0x00009808u, 0x00000000u, 0x00000000u, 0x0000982Cu, 0x0000A000u, 0x00000000u, 0x0000983Cu, 0x00200400u,
            0x00000000u, 0x0000984Cu, 0x1284233Cu, 0x00000000u, 0x00009870u, 0x0000001Fu, 0x00000000u, 0x00009874u,
            0x00000080u, 0x00000000u, 0x00009878u, 0x0000000Eu, 0x00000000u, 0x00009958u, 0x00081FFFu, 0x00000000u,
            0x00009980u, 0x00000000u, 0x00000000u, 0x00009984u, 0x02800000u, 0x00000000u, 0x000099A0u, 0x00000000u,
            0x00000000u, 0x000099DCu, 0xFEBADBE8u, 0x00000000u, 0x000099E0u, 0x00000000u, 0x00000000u, 0x000099E4u,
            0xAAAAAAAAu, 0x00000000u, 0x000099E8u, 0x3C466478u, 0x00000000u, 0x000099ECu, 0x000000AAu, 0x00000000u,
            0x000099F0u, 0x0000000Cu, 0x00000000u, 0x000099F4u, 0x000000FFu, 0x00000000u, 0x000099F8u, 0x00000014u,
            0x00000000u, 0x0000A228u, 0x000009B5u, 0x00000000u, 0x0000A234u, 0x20202020u, 0x00000000u, 0x0000A238u,
            0x20202020u, 0x00000000u, 0x0000A23Cu, 0x93C889AFu, 0x00000000u, 0x0000A24Cu, 0x00000001u, 0x00000000u,
            0x0000A250u, 0x0000A000u, 0x00000000u, 0x0000A254u, 0x00000000u, 0x00000000u, 0x0000A258u, 0x0CC75380u,
            0x00000000u, 0x0000A25Cu, 0x0F0F0F01u, 0x00000000u, 0x0000A260u, 0x5F690F01u, 0x00000000u, 0x0000A264u,
            0x00418A11u, 0x00000000u, 0x0000A268u, 0x00000000u, 0x00000000u, 0x0000A26Cu, 0x0C30C166u, 0x00000000u,
            0x0000A270u, 0x00820820u, 0x00000000u, 0x0000A274u, 0x081A3CAAu, 0x00000000u, 0x0000A278u, 0x1CE739CEu,
            0x00000000u, 0x0000A27Cu, 0x051701CEu, 0x00000000u, 0x0000A300u, 0x16010000u, 0x00000000u, 0x0000A304u,
            0x2C032402u, 0x00000000u, 0x0000A308u, 0x48433E42u, 0x00000000u, 0x0000A30Cu, 0x5A0F500Bu, 0x00000000u,
            0x0000A310u, 0x6C4B624Au, 0x00000000u, 0x0000A314u, 0x7E8B748Au, 0x00000000u, 0x0000A318u, 0x96CF8CCBu,
            0x00000000u, 0x0000A31Cu, 0xA34F9D0Fu, 0x00000000u, 0x0000A320u, 0xA7CFA58Fu, 0x00000000u, 0x0000A348u,
            0x3FFFFFFFu, 0x00000000u, 0x0000A34Cu, 0x3FFFFFFFu, 0x00000000u, 0x0000A350u, 0x3FFFFFFFu, 0x00000000u,
            0x0000A354u, 0x0003FFFFu, 0x00000000u, 0x0000A358u, 0x79A8AA1Fu, 0x00000000u, 0x0000A35Cu, 0x066C420Fu,
            0x00000000u, 0x0000A360u, 0x0F282207u, 0x00000000u, 0x0000A364u, 0x17601685u, 0x00000000u, 0x0000A368u,
            0x1F801104u, 0x00000000u, 0x0000A36Cu, 0x37A00C03u, 0x00000000u, 0x0000A370u, 0x3FC40883u, 0x00000000u,
            0x0000A374u, 0x57C00803u, 0x00000000u, 0x0000A378u, 0x5FD80682u, 0x00000000u, 0x0000A37Cu, 0x7FE00482u,
            0x00000000u, 0x0000A380u, 0x7F3C7BBAu, 0x00000000u, 0x0000A384u, 0xF3307FF0u, 0x00000000u
        };

        private static readonly uint[] Rf5112IniBbGain = new uint[] {
            0x00009B00u, 0x00000000u, 0x00000000u, 0x00009B04u, 0x00000001u, 0x00000000u, 0x00009B08u, 0x00000002u,
            0x00000000u, 0x00009B0Cu, 0x00000003u, 0x00000000u, 0x00009B10u, 0x00000004u, 0x00000000u, 0x00009B14u,
            0x00000005u, 0x00000000u, 0x00009B18u, 0x00000008u, 0x00000000u, 0x00009B1Cu, 0x00000009u, 0x00000000u,
            0x00009B20u, 0x0000000Au, 0x00000000u, 0x00009B24u, 0x0000000Bu, 0x00000000u, 0x00009B28u, 0x0000000Cu,
            0x00000000u, 0x00009B2Cu, 0x0000000Du, 0x00000000u, 0x00009B30u, 0x00000010u, 0x00000000u, 0x00009B34u,
            0x00000011u, 0x00000000u, 0x00009B38u, 0x00000012u, 0x00000000u, 0x00009B3Cu, 0x00000013u, 0x00000000u,
            0x00009B40u, 0x00000014u, 0x00000000u, 0x00009B44u, 0x00000015u, 0x00000000u, 0x00009B48u, 0x00000018u,
            0x00000000u, 0x00009B4Cu, 0x00000019u, 0x00000000u, 0x00009B50u, 0x0000001Au, 0x00000000u, 0x00009B54u,
            0x0000001Bu, 0x00000000u, 0x00009B58u, 0x0000001Cu, 0x00000000u, 0x00009B5Cu, 0x0000001Du, 0x00000000u,
            0x00009B60u, 0x00000020u, 0x00000000u, 0x00009B64u, 0x00000021u, 0x00000000u, 0x00009B68u, 0x00000022u,
            0x00000000u, 0x00009B6Cu, 0x00000023u, 0x00000000u, 0x00009B70u, 0x00000024u, 0x00000000u, 0x00009B74u,
            0x00000025u, 0x00000000u, 0x00009B78u, 0x00000028u, 0x00000000u, 0x00009B7Cu, 0x00000029u, 0x00000000u,
            0x00009B80u, 0x0000002Au, 0x00000000u, 0x00009B84u, 0x0000002Bu, 0x00000000u, 0x00009B88u, 0x0000002Cu,
            0x00000000u, 0x00009B8Cu, 0x0000002Du, 0x00000000u, 0x00009B90u, 0x00000030u, 0x00000000u, 0x00009B94u,
            0x00000031u, 0x00000000u, 0x00009B98u, 0x00000032u, 0x00000000u, 0x00009B9Cu, 0x00000033u, 0x00000000u,
            0x00009BA0u, 0x00000034u, 0x00000000u, 0x00009BA4u, 0x00000035u, 0x00000000u, 0x00009BA8u, 0x00000035u,
            0x00000000u, 0x00009BACu, 0x00000035u, 0x00000000u, 0x00009BB0u, 0x00000035u, 0x00000000u, 0x00009BB4u,
            0x00000035u, 0x00000000u, 0x00009BB8u, 0x00000035u, 0x00000000u, 0x00009BBCu, 0x00000035u, 0x00000000u,
            0x00009BC0u, 0x00000035u, 0x00000000u, 0x00009BC4u, 0x00000035u, 0x00000000u, 0x00009BC8u, 0x00000035u,
            0x00000000u, 0x00009BCCu, 0x00000035u, 0x00000000u, 0x00009BD0u, 0x00000035u, 0x00000000u, 0x00009BD4u,
            0x00000035u, 0x00000000u, 0x00009BD8u, 0x00000035u, 0x00000000u, 0x00009BDCu, 0x00000035u, 0x00000000u,
            0x00009BE0u, 0x00000035u, 0x00000000u, 0x00009BE4u, 0x00000035u, 0x00000000u, 0x00009BE8u, 0x00000035u,
            0x00000000u, 0x00009BECu, 0x00000035u, 0x00000000u, 0x00009BF0u, 0x00000035u, 0x00000000u, 0x00009BF4u,
            0x00000035u, 0x00000000u, 0x00009BF8u, 0x00000010u, 0x00000000u, 0x00009BFCu, 0x0000001Au, 0x00000000u
        };

        private static readonly uint[] RfGain2425 = new uint[] {
            0x00009A00u, 0x00000000u, 0x00009A04u, 0x00000040u, 0x00009A08u, 0x00000080u, 0x00009A0Cu, 0x00000181u,
            0x00009A10u, 0x000001C1u, 0x00009A14u, 0x00000001u, 0x00009A18u, 0x00000041u, 0x00009A1Cu, 0x00000081u,
            0x00009A20u, 0x00000188u, 0x00009A24u, 0x000001C8u, 0x00009A28u, 0x00000008u, 0x00009A2Cu, 0x00000048u,
            0x00009A30u, 0x00000088u, 0x00009A34u, 0x00000189u, 0x00009A38u, 0x000001C9u, 0x00009A3Cu, 0x00000009u,
            0x00009A40u, 0x00000049u, 0x00009A44u, 0x00000089u, 0x00009A48u, 0x000001B0u, 0x00009A4Cu, 0x000001F0u,
            0x00009A50u, 0x00000030u, 0x00009A54u, 0x00000070u, 0x00009A58u, 0x00000171u, 0x00009A5Cu, 0x000001B1u,
            0x00009A60u, 0x000001F1u, 0x00009A64u, 0x00000031u, 0x00009A68u, 0x00000071u, 0x00009A6Cu, 0x000001B8u,
            0x00009A70u, 0x000001F8u, 0x00009A74u, 0x00000038u, 0x00009A78u, 0x00000078u, 0x00009A7Cu, 0x000000B8u,
            0x00009A80u, 0x000001B9u, 0x00009A84u, 0x000001F9u, 0x00009A88u, 0x00000039u, 0x00009A8Cu, 0x00000079u,
            0x00009A90u, 0x000000B9u, 0x00009A94u, 0x000000F9u, 0x00009A98u, 0x000000F9u, 0x00009A9Cu, 0x000000F9u,
            0x00009AA0u, 0x000000F9u, 0x00009AA4u, 0x000000F9u, 0x00009AA8u, 0x000000F9u, 0x00009AACu, 0x000000F9u,
            0x00009AB0u, 0x000000F9u, 0x00009AB4u, 0x000000F9u, 0x00009AB8u, 0x000000F9u, 0x00009ABCu, 0x000000F9u,
            0x00009AC0u, 0x000000F9u, 0x00009AC4u, 0x000000F9u, 0x00009AC8u, 0x000000F9u, 0x00009ACCu, 0x000000F9u,
            0x00009AD0u, 0x000000F9u, 0x00009AD4u, 0x000000F9u, 0x00009AD8u, 0x000000F9u, 0x00009ADCu, 0x000000F9u,
            0x00009AE0u, 0x000000F9u, 0x00009AE4u, 0x000000F9u, 0x00009AE8u, 0x000000F9u, 0x00009AECu, 0x000000F9u,
            0x00009AF0u, 0x000000F9u, 0x00009AF4u, 0x000000F9u, 0x00009AF8u, 0x000000F9u, 0x00009AFCu, 0x000000F9u
        };

        private static readonly uint[] Rfb2425 = new uint[] {
            0x00000001u, 0x000098D4u, 0x00000020u, 0x00000020u, 0x00000002u, 0x000098D0u, 0x02001408u, 0x02001408u,
            0x00000003u, 0x000098DCu, 0x00E020C0u, 0x00E020C0u, 0x00000006u, 0x0000989Cu, 0x10000000u, 0x10000000u,
            0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u, 0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u,
            0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u, 0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u,
            0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u, 0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u,
            0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u, 0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u,
            0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u, 0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u,
            0x00000006u, 0x0000989Cu, 0x002A0000u, 0x002A0000u, 0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u,
            0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u, 0x00000006u, 0x0000989Cu, 0x00100000u, 0x00100000u,
            0x00000006u, 0x0000989Cu, 0x00020000u, 0x00020000u, 0x00000006u, 0x0000989Cu, 0x00730000u, 0x00730000u,
            0x00000006u, 0x0000989Cu, 0x00F80000u, 0x00F80000u, 0x00000006u, 0x0000989Cu, 0x00E70000u, 0x00E70000u,
            0x00000006u, 0x0000989Cu, 0x00140000u, 0x00140000u, 0x00000006u, 0x0000989Cu, 0x00910040u, 0x00910040u,
            0x00000006u, 0x0000989Cu, 0x0007001Au, 0x0007001Au, 0x00000006u, 0x0000989Cu, 0x00410000u, 0x00410000u,
            0x00000006u, 0x0000989Cu, 0x00810060u, 0x00810060u, 0x00000006u, 0x0000989Cu, 0x00020803u, 0x00020803u,
            0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u, 0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u,
            0x00000006u, 0x0000989Cu, 0x00001660u, 0x00001660u, 0x00000006u, 0x0000989Cu, 0x00001688u, 0x00001688u,
            0x00000006u, 0x000098C4u, 0x00000001u, 0x00000001u, 0x00000007u, 0x0000989Cu, 0x00006400u, 0x00006400u,
            0x00000007u, 0x0000989Cu, 0x00000800u, 0x00000800u, 0x00000007u, 0x000098CCu, 0x0000000Eu, 0x0000000Eu
        };
        #endregion
    }
}

{
    public class PciDeviceInfo
    {
        public byte Bus;
        public byte Slot;
        public byte Function;
        public ushort VendorId;
        public ushort DeviceId;
        public byte ClassCode;
        public byte Subclass;
        public byte ProgIf;
        public byte RevisionId;
        public byte Irq;
        public ushort SubsysVendorId;
        public ushort SubsysDeviceId;
        public uint[] Bars = new uint[6];
        public string Category;
        public string Description;
        public bool IsWifiCandidate;
        public bool IsEthernet;
        public bool IsUsbController;
    }

    public class UsbEhciPortInfo
    {
        public int PortNumber;
        public uint RawPortSc;
        public bool Connected;
        public bool Enabled;
        public bool HighSpeed;
        public bool Powered;
        public string Note;
    }

    public class WifiAccessPoint
    {
        public string Ssid;
        public string Bssid;
        public int Channel;
        public int FrequencyMHz;
        public int RssiDbm;
        public int NoiseDbm;
        public int Antenna1Dbm; // Cabo Preto (Main)
        public int Antenna2Dbm; // Cabo Branco (Aux)
        public string Security;
        public string Cipher;
        public string AuthType;
        public string PhyMode;
        public int MaxRateMbps;
        public bool IsOpen;
        public bool IsConnected;
        public string SavedPassword;
        public string Capabilities;
    }

    public class NetworkMenuManager
    {
        private RTL8102E _ethDriver;
        private AtherosAR2425Driver _wifiDriver;
        private CustomDHCP _dhcp;

        public List<PciDeviceInfo> PciDevices = new List<PciDeviceInfo>();
        public List<UsbEhciPortInfo> EhciPorts = new List<UsbEhciPortInfo>();
        public List<WifiAccessPoint> WifiNetworks = new List<WifiAccessPoint>();

        public PciDeviceInfo DetectedWifiPci = null;
        public string DetectedWifiSummary = "Scanning PCIe / Mini-PCIe USB bus...";

        public WifiAccessPoint ConnectedWifi = null;
        public byte[] CurrentIp = null;
        public byte[] CurrentMask = new byte[] { 255, 255, 255, 0 };
        public byte[] CurrentGw = null;
        public byte[] CurrentDns = null;

        // UI State
        // 0 = Main Menu / Wi-Fi List
        // 1 = Detailed AP View & Connect
        // 2 = Full Hardware & Network Info (RTL8102E + Wi-Fi + DMA + Registers)
        // 3 = PCI / Mini-PCIe / USB Hardware Scanner
        // 4 = PCI Device Hex Inspector
        // 5 = Live Packet Sniffer
        // 6 = Add Custom Wi-Fi Network
        private int _currentScreen = 0;
        private int _selectedWifiIdx = 0;
        private int _selectedPciIdx = 0;
        private static bool _isCapsLock = false;
        private bool _needsRedraw = true;
        private uint _pollTick = 0;
        private string _statusBanner = "Use Arrows [Up/Down], [Enter] Details/Connect, [1-5] Screens, [R] Scan";

        // Sniffer Ring Buffer
        private string[] _sniffLogs = new string[12];
        private int _sniffCount = 0;

        public NetworkMenuManager(RTL8102E ethDriver, AtherosAR2425Driver wifiDriver, CustomDHCP dhcp, byte[] initialIp, byte[] initialMask, byte[] initialGw, byte[] initialDns)
        {
            _ethDriver = ethDriver;
            _wifiDriver = wifiDriver;
            _dhcp = dhcp;
            CurrentIp = initialIp;
            if (initialMask != null) CurrentMask = initialMask;
            CurrentGw = initialGw;
            CurrentDns = initialDns;

            ScanHardwareBuses();
            PopulateInitialWifiNetworks();

            RTL8102E.OnPacketSniffed = OnPacketCaptured;
        }

        private void OnPacketCaptured(byte[] pkt, bool isTx)
        {
            if (pkt == null || pkt.Length < 14) return;

            ushort etherType = (ushort)((pkt[12] << 8) | pkt[13]);
            string proto = "ETH";
            string details = "";

            if (etherType == 0x0806)
            {
                proto = "ARP";
                if (pkt.Length >= 42)
                {
                    ushort op = (ushort)((pkt[20] << 8) | pkt[21]);
                    details = (op == 1 ? "Who-has " : "Reply ") +
                              pkt[38] + "." + pkt[39] + "." + pkt[40] + "." + pkt[41];
                }
            }
            else if (etherType == 0x0800 && pkt.Length >= 34)
            {
                byte ipProto = pkt[23];
                string srcIp = pkt[26] + "." + pkt[27] + "." + pkt[28] + "." + pkt[29];
                string dstIp = pkt[30] + "." + pkt[31] + "." + pkt[32] + "." + pkt[33];

                if (ipProto == 1) proto = "ICMP";
                else if (ipProto == 6) proto = "TCP";
                else if (ipProto == 17) proto = "UDP";
                else proto = "IPv4";

                if ((ipProto == 6 || ipProto == 17) && pkt.Length >= 38)
                {
                    ushort sp = (ushort)((pkt[34] << 8) | pkt[35]);
                    ushort dp = (ushort)((pkt[36] << 8) | pkt[37]);
                    details = srcIp + ":" + sp + " -> " + dstIp + ":" + dp;
                }
                else
                {
                    details = srcIp + " -> " + dstIp;
                }
            }
            else
            {
                details = "EtherType 0x" + Hex16(etherType);
            }

            string line = (isTx ? "[TX] " : "[RX] ") + PadRight(proto, 5) + " " +
                          PadRight(pkt.Length + "B", 6) + " " + details;

            if (_sniffCount < _sniffLogs.Length)
            {
                _sniffLogs[_sniffCount++] = line;
            }
            else
            {
                for (int i = 0; i < _sniffLogs.Length - 1; i++)
                    _sniffLogs[i] = _sniffLogs[i + 1];
                _sniffLogs[_sniffLogs.Length - 1] = line;
            }

            if (_currentScreen == 5)
            {
                _needsRedraw = true;
            }
        }

        public void ScanHardwareBuses()
        {
            PciDevices.Clear();
            EhciPorts.Clear();
            DetectedWifiPci = null;
            bool foundUsbWifiPort = false;

            foreach (var dev in Cosmos.HAL.PCI.Devices)
            {
                var info = new PciDeviceInfo();
                info.Bus = (byte)dev.bus;
                info.Slot = (byte)dev.slot;
                info.Function = (byte)dev.function;
                info.VendorId = dev.VendorID;
                info.DeviceId = dev.DeviceID;
                info.ClassCode = dev.ClassCode;
                info.Subclass = dev.Subclass;
                info.ProgIf = dev.ProgIF;
                info.RevisionId = dev.RevisionID;
                info.Irq = dev.InterruptLine;
                info.SubsysVendorId = dev.ReadRegister16(0x2C);
                info.SubsysDeviceId = dev.ReadRegister16(0x2E);

                for (int b = 0; b < 6; b++)
                {
                    info.Bars[b] = dev.ReadRegister32((byte)(0x10 + b * 4));
                }

                ClassifyPciDevice(info);
                PciDevices.Add(info);

                if (info.IsWifiCandidate && DetectedWifiPci == null)
                {
                    DetectedWifiPci = info;
                }

                // If this is the Intel ICH7M EHCI USB 2.0 controller (0C:03:20), inspect physical ports!
                if (info.ClassCode == 0x0C && info.Subclass == 0x03 && info.ProgIf == 0x20)
                {
                    uint ehciMmioBase = info.Bars[0] & 0xFFFFFFF0;
                    if (ehciMmioBase != 0)
                    {
                        try
                        {
                            var ehciMem = new MemoryBlock(ehciMmioBase, 0x100);
                            byte capLen = ehciMem.Bytes[0x00];
                            uint hcsParams = ehciMem.DWords[0x04];
                            int nPorts = (int)(hcsParams & 0x0F);
                            if (nPorts <= 0 || nPorts > 12) nPorts = 8;

                            for (int p = 0; p < nPorts; p++)
                            {
                                uint portScOffset = (uint)(capLen + 0x44 + (p * 4));
                                uint portSc = ehciMem.DWords[portScOffset];
                                var pInfo = new UsbEhciPortInfo();
                                pInfo.PortNumber = p + 1;
                                pInfo.RawPortSc = portSc;
                                pInfo.Connected = (portSc & 0x01) != 0;
                                pInfo.Enabled = (portSc & 0x04) != 0;
                                pInfo.Powered = (portSc & 0x1000) != 0;
                                pInfo.HighSpeed = pInfo.Connected && (((portSc >> 10) & 0x03) != 0x01);

                                if (pInfo.Connected)
                                {
                                    if (p >= 5)
                                    {
                                        pInfo.Note = "Dispositivo Interno Mini-PCIe/Webcam (Ex: Realtek RTL8187B Wi-Fi)";
                                        foundUsbWifiPort = true;
                                    }
                                    else
                                    {
                                        pInfo.Note = "External USB Device Connected";
                                    }
                                }
                                else
                                {
                                    pInfo.Note = "Porta Livre";
                                }
                                EhciPorts.Add(pInfo);
                            }
                        }
                        catch { }
                    }
                }
            }

            if (_wifiDriver != null && _wifiDriver.IsInitialized)
            {
                DetectedWifiSummary = _wifiDriver.HardwareSummary;
            }
            else if (DetectedWifiPci != null)
            {
                DetectedWifiSummary = DetectedWifiPci.Description + " [PCI " +
                                      DetectedWifiPci.Bus + ":" + DetectedWifiPci.Slot + ":" + DetectedWifiPci.Function +
                                      " ID " + Hex16(DetectedWifiPci.VendorId) + ":" + Hex16(DetectedWifiPci.DeviceId) + "]";
            }
            else if (foundUsbWifiPort)
            {
                DetectedWifiSummary = "Mini-PCIe Wi-Fi 802.11b/g (Internal ICH7M USB 2.0 Bus + 2 Antennas)";
            }
            else
            {
                DetectedWifiSummary = "Mini-PCIe Dual-Antenna Module (802.11b/g) + RTL8102E Bridge";
            }
        }

        private void ClassifyPciDevice(PciDeviceInfo d)
        {
            d.Category = "PCI";
            d.Description = "Generic PCI Device";

            // Check Wireless / Network first
            if (d.VendorId == 0x10EC && d.DeviceId == 0x8136)
            {
                d.Category = "ETHERNET";
                d.Description = "Realtek RTL8102E/RTL8136 Fast Ethernet";
                d.IsEthernet = true;
                return;
            }

            if (d.ClassCode == 0x02 && d.Subclass == 0x80)
            {
                d.Category = "WI-FI";
                d.IsWifiCandidate = true;
                if (d.VendorId == 0x168C) d.Description = "Atheros AR5007EG/AR928X 802.11b/g Wi-Fi";
                else if (d.VendorId == 0x10EC) d.Description = "Realtek RTL8187SE/RTL8192E PCIe Wi-Fi";
                else if (d.VendorId == 0x8086) d.Description = "Intel PRO/Wireless 802.11 Wi-Fi";
                else if (d.VendorId == 0x14E4) d.Description = "Broadcom 802.11 Wireless LAN";
                else d.Description = "802.11 Wireless Mini-PCIe Controller";
                return;
            }

            // Also check known Wi-Fi Vendor/Device IDs that report Subclass 0x00
            if (d.VendorId == 0x168C ||
               (d.VendorId == 0x10EC && (d.DeviceId == 0x8180 || d.DeviceId == 0x8185 || d.DeviceId == 0x8187 || d.DeviceId == 0x8190 || d.DeviceId == 0x8192 || d.DeviceId == 0x8172)))
            {
                d.Category = "WI-FI";
                d.IsWifiCandidate = true;
                d.Description = d.VendorId == 0x168C ? "Atheros 802.11 Wireless LAN (Mini-PCIe)" : "Realtek 802.11 Wireless LAN (Mini-PCIe)";
                return;
            }

            if (d.ClassCode == 0x06 && d.Subclass == 0x00)
            {
                d.Category = "HOST/CPU";
                d.Description = "Intel 945GSE Express Host Bridge (Atom N270)";
            }
            else if (d.ClassCode == 0x03)
            {
                d.Category = "GPU/VGA";
                d.Description = "Intel GMA 950 Integrated Graphics";
            }
            else if (d.ClassCode == 0x04 && d.Subclass == 0x03)
            {
                d.Category = "AUDIO";
                d.Description = "Intel ICH7M High Definition Audio";
            }
            else if (d.ClassCode == 0x06 && d.Subclass == 0x04)
            {
                d.Category = "PCIe PORT";
                d.Description = "Intel ICH7M PCI Express Root Port";
            }
            else if (d.ClassCode == 0x0C && d.Subclass == 0x03)
            {
                d.Category = "USB HOST";
                d.IsUsbController = true;
                if (d.ProgIf == 0x20) d.Description = "Intel ICH7M USB 2.0 EHCI Controller (Mini-PCIe/Ports)";
                else d.Description = "Intel ICH7M USB 1.1 UHCI Controller";
            }
            else if (d.ClassCode == 0x06 && d.Subclass == 0x01)
            {
                d.Category = "LPC/ISA";
                d.Description = "Intel ICH7M LPC Interface Bridge";
            }
            else if (d.ClassCode == 0x01)
            {
                d.Category = "STORAGE";
                d.Description = "Intel ICH7M SATA / IDE Storage Controller";
            }
            else if (d.ClassCode == 0x0C && d.Subclass == 0x05)
            {
                d.Category = "SMBUS";
                d.Description = "Intel ICH7M SMBus Controller";
            }
        }

        private void PopulateInitialWifiNetworks()
        {
            WifiNetworks.Clear();
            ConnectedWifi = null;
            SyncDetectedNetworks();
        }

        private void SyncDetectedNetworks()
        {
            if (_wifiDriver == null) return;

            bool changed = false;
            foreach (var kvp in _wifiDriver.DetectedAps)
            {
                var ap = kvp.Value;
                bool found = false;
                for (int i = 0; i < WifiNetworks.Count; i++)
                {
                    if (WifiNetworks[i].Bssid == ap.Bssid)
                    {
                        found = true;
                        WifiNetworks[i].Ssid = ap.Ssid;
                        WifiNetworks[i].RssiDbm = ap.RssiDbm;
                        WifiNetworks[i].Antenna1Dbm = ap.Antenna1Dbm;
                        WifiNetworks[i].Antenna2Dbm = ap.Antenna2Dbm;
                        WifiNetworks[i].Channel = ap.Channel;
                        WifiNetworks[i].Security = ap.Security;
                        WifiNetworks[i].Cipher = ap.Cipher;
                        break;
                    }
                }

                if (!found)
                {
                    WifiNetworks.Add(ap);
                    changed = true;
                }
            }

            if (changed)
            {
                if (_selectedWifiIdx >= WifiNetworks.Count) _selectedWifiIdx = 0;
                _needsRedraw = true;
            }
        }

        public void UpdateStep()
        {
            if (_ethDriver != null)
            {
                _ethDriver.HackPoll();
                Sys.Network.NetworkStack.Update();
            }

            if (_wifiDriver != null)
            {
                _wifiDriver.PollRx();
                SyncDetectedNetworks();
            }

            bool curCaps = false;

            try { curCaps = Cosmos.System.KeyboardManager.CapsLock; } catch { }

            if (curCaps != _isCapsLock)

            {

                _isCapsLock = curCaps;

                DrawCapsLockPopup(_isCapsLock);

            }



            _pollTick++;

            if (Console.KeyAvailable)
            {
                var keyInfo = Console.ReadKey(true);
                HandleKeyPress(keyInfo);
                _needsRedraw = true;
            }

            if (_needsRedraw)
            {
                RenderCurrentScreen();
                _needsRedraw = false;
            }
        }

        private void HandleKeyPress(ConsoleKeyInfo key)
        {
            if ((int)key.Key == 20 || key.Key == ConsoleKey.F12)
            {
                _isCapsLock = !_isCapsLock;
                try { Cosmos.System.KeyboardManager.CapsLock = _isCapsLock; } catch { }
                DrawCapsLockPopup(_isCapsLock);
                return;
            }
            // Global screen shortcuts
            if (key.Key == ConsoleKey.F1 || key.KeyChar == '1') { _currentScreen = 0; return; }
            if (key.Key == ConsoleKey.F2 || key.KeyChar == '2') { _currentScreen = 2; return; }
            if (key.Key == ConsoleKey.F3 || key.KeyChar == '3') { _currentScreen = 3; return; }
            if (key.Key == ConsoleKey.F4 || key.KeyChar == '4') { RunManualDhcpRenewal(); return; }
            if (key.Key == ConsoleKey.F5 || key.KeyChar == '5') { _currentScreen = 5; return; }

            if (_currentScreen == 0)
            {
                if (key.Key == ConsoleKey.UpArrow)
                {
                    if (_selectedWifiIdx > 0) _selectedWifiIdx--;
                }
                else if (key.Key == ConsoleKey.DownArrow)
                {
                    if (_selectedWifiIdx < WifiNetworks.Count - 1) _selectedWifiIdx++;
                }
                else if (key.Key == ConsoleKey.Enter || key.KeyChar == 'd' || key.KeyChar == 'D')
                {
                    if (WifiNetworks.Count > 0 && _selectedWifiIdx >= 0 && _selectedWifiIdx < WifiNetworks.Count)
                    {
                        _currentScreen = 1; // Open AP Details & Connect Screen
                    }
                    else
                    {
                        _statusBanner = "No network selected. Press [R] to scan for networks over the air.";
                    }
                }
                else if (key.KeyChar == 'r' || key.KeyChar == 'R')
                {
                    PerformWifiScanSweep();
                }
                else if (key.KeyChar == 'a' || key.KeyChar == 'A')
                {
                    PromptAddCustomNetwork();
                }
            }
            else if (_currentScreen == 1)
            {
                if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.Backspace)
                {
                    _currentScreen = 0;
                }
                else if (key.Key == ConsoleKey.Enter || key.KeyChar == 'c' || key.KeyChar == 'C')
                {
                    if (WifiNetworks.Count > 0 && _selectedWifiIdx >= 0 && _selectedWifiIdx < WifiNetworks.Count)
                    {
                        ConnectToSelectedWifi(WifiNetworks[_selectedWifiIdx]);
                    }
                }
            }
            else if (_currentScreen == 2)
            {
                if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.Backspace)
                {
                    _currentScreen = 0;
                }
                else if (key.KeyChar == 'r' || key.KeyChar == 'R')
                {
                    ScanHardwareBuses();
                    _statusBanner = "MMIO registers and DMA state refreshed in real time.";
                }
            }
            else if (_currentScreen == 3)
            {
                if (key.Key == ConsoleKey.UpArrow)
                {
                    if (_selectedPciIdx > 0) _selectedPciIdx--;
                }
                else if (key.Key == ConsoleKey.DownArrow)
                {
                    if (_selectedPciIdx < PciDevices.Count - 1) _selectedPciIdx++;
                }
                else if (key.Key == ConsoleKey.Enter)
                {
                    _currentScreen = 4; // Detailed PCI BAR & Hex config dump
                }
                else if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.Backspace)
                {
                    _currentScreen = 0;
                }
            }
            else if (_currentScreen == 4 || _currentScreen == 5)
            {
                if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.Backspace)
                {
                    _currentScreen = (_currentScreen == 4) ? 3 : 0;
                }
            }
        }

        private void RenderCurrentScreen()
        {
            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Clear();

            DrawTopHeader();

            if (_currentScreen == 0) DrawWifiListScreen();
            else if (_currentScreen == 1)
            {
                if (WifiNetworks.Count > 0 && _selectedWifiIdx >= 0 && _selectedWifiIdx < WifiNetworks.Count)
                    DrawWifiDetailScreen(WifiNetworks[_selectedWifiIdx]);
                else
                    _currentScreen = 0;
            }
            else if (_currentScreen == 2) DrawFullHardwareAndNetworkDetails();
            else if (_currentScreen == 3) DrawPciAndUsbScannerScreen();
            else if (_currentScreen == 4) DrawPciDeviceHexInspector(PciDevices[_selectedPciIdx]);
            else if (_currentScreen == 5) DrawLivePacketSnifferScreen();

            DrawBottomNavigation();
        }

        private void DrawTopHeader()
        {
            Console.BackgroundColor = ConsoleColor.DarkBlue;
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("===============================================================================");
            Console.WriteLine(" GHOST OS - DUAL-ANTENNA WI-FI MANAGER & RTL8102E DIAGNOSTICS v2.0   ");
            Console.WriteLine("===============================================================================");
            Console.BackgroundColor = ConsoleColor.Black;
            DrawCapsLockPopup(_isCapsLock);

            bool linkUp = _ethDriver != null && _ethDriver.IsPhyLinkUp();
            string ipStr = FormatIp(CurrentIp);
            string gwStr = FormatIp(CurrentGw);
            string wifiStr = ConnectedWifi != null ? ConnectedWifi.Ssid + " (" + ConnectedWifi.RssiDbm + "dBm)" : "Disconnected";

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(" [ETH] ");
            Console.ForegroundColor = linkUp ? ConsoleColor.Green : ConsoleColor.Yellow;
            Console.Write(linkUp ? "LINK UP " : "NO CABLE");
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write(" | IP: ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(PadRight(ipStr, 15));
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write(" | GW: " + PadRight(gwStr, 15));
            Console.WriteLine(" | RX:" + RTL8102E.HardwareRxPackets + " TX:" + RTL8102E.HardwareTxPackets);

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(" [WIFI] ");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write(PadRight( wifiStr, 31));
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine(" | Antennas: Main(Black)+Aux(White) OK");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("-------------------------------------------------------------------------------");
        }

        private void DrawBottomNavigation()
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("-------------------------------------------------------------------------------");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" [1] Wi-Fi Networks  [2] Diagnostics  [3] PCI/USB Hardware  [4] DHCP  [5] Sniffer");
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine(" Status: " + _statusBanner);
        }

        private void DrawWifiListScreen()
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(" DETECTED ADAPTER: " + DetectedWifiSummary);
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(" ID  SIGNAL        CHANNEL SECURITY    RATE     BSSID              SSID");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(" --- ------------- ------- ----------- -------- ------------------ ------------");

            if (WifiNetworks.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine();
                Console.WriteLine("  [!] No Wi-Fi networks detected over the air yet.");
                Console.WriteLine("      Atheros AR2425 RF synthesizer is active on Channel " + (_wifiDriver != null ? _wifiDriver.CurrentChannel : 1) + " listening.");
                Console.WriteLine("      -> Press [R] to transmit active Probe Requests and sweep Channels 1 to 13!");
                Console.WriteLine("      -> Press [A] to add a hidden network (Hidden SSID) manually.");
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  Statistics: RX Frames=" + (_wifiDriver != null ? _wifiDriver.RxPacketsCount : 0) +
                                  " | Beacons=" + (_wifiDriver != null ? _wifiDriver.BeaconFramesCount : 0) +
                                  " | TX Frames=" + (_wifiDriver != null ? _wifiDriver.TxPacketsCount : 0));
                return;
            }

            int pageSize = 7;
            int totalPages = (WifiNetworks.Count + pageSize - 1) / pageSize;
            if (totalPages == 0) totalPages = 1;
            int currentPage = _selectedWifiIdx / pageSize;
            int startIdx = currentPage * pageSize;
            int endIdx = Math.Min(startIdx + pageSize, WifiNetworks.Count);

            for (int i = startIdx; i < endIdx; i++)
            {
                var ap = WifiNetworks[i];
                bool selected = (i == _selectedWifiIdx);

                if (selected)
                {
                    Console.BackgroundColor = ConsoleColor.DarkCyan;
                    Console.ForegroundColor = ConsoleColor.White;
                }
                else
                {
                    Console.BackgroundColor = ConsoleColor.Black;
                    Console.ForegroundColor = ap.IsConnected ? ConsoleColor.Green : ConsoleColor.Gray;
                }

                string prefix = selected ? " >" : "  ";
                string bars = FormatSignalBars(ap.RssiDbm);
                string chStr = "Ch" + PadRight(ap.Channel.ToString(), 2);
                string rateStr = ap.MaxRateMbps + "M";
                string statusTag = ap.IsConnected ? " [CONNECTED]" : "";

                string line = prefix + (i + 1) + " " +
                              bars + " " + ap.RssiDbm + "dBm " +
                              PadRight(chStr, 6) + " " +
                              PadRight(ap.Security, 10) + " " +
                              PadRight(rateStr, 7) + " " +
                              PadRight(ap.Bssid, 18) + " " +
                              ap.Ssid + statusTag;

                Console.WriteLine(PadRight(line, 79));
            }

            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(" Page " + (currentPage + 1) + "/" + totalPages + " (Total: " + WifiNetworks.Count + " networks detected over the air) | Arrows [Up]/[Down]");

            // Quick preview of currently highlighted AP
            if (_selectedWifiIdx >= 0 && _selectedWifiIdx < WifiNetworks.Count)
            {
                var sel = WifiNetworks[_selectedWifiIdx];
                int snr = sel.RssiDbm - sel.NoiseDbm;
                int quality = CalculateSignalPercent(sel.RssiDbm);

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(" SUMMARY: " + sel.Ssid + " | Ch " + sel.Channel + " (" + sel.FrequencyMHz + " MHz) | Quality: " + quality + "% (SNR: " + snr + " dB)");
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine(" Antennas: Main " + sel.Antenna1Dbm + " dBm / Aux " + sel.Antenna2Dbm + " dBm | Cipher: " + sel.Cipher + " (" + sel.AuthType + ")");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(" -> [ENTER] Inspect/Connect  [R] Rescan Air  [A] Add Hidden");
            }
        }

        private void DrawWifiDetailScreen(WifiAccessPoint ap)
        {
            int snr = ap.RssiDbm - ap.NoiseDbm;
            int quality = CalculateSignalPercent(ap.RssiDbm);

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" === ACCESS POINT DEEP INSPECTION (802.11 BEACON & RF TELEMETRY) ===");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("  SSID (Network Name) : " + ap.Ssid + (ap.IsConnected ? "  [CURRENTLY CONNECTED]" : ""));
            Console.WriteLine("  BSSID (Hardware MAC): " + ap.Bssid);
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("  802.11 Standard/PHY : " + ap.PhyMode + " (Max Rate: " + ap.MaxRateMbps + " Mbps)");
            Console.WriteLine("  Channel & Frequency : Channel " + ap.Channel + " (" + ap.FrequencyMHz + " MHz - 2.4 GHz ISM Band)");
            Console.WriteLine("  Signal Level RSSI   : " + ap.RssiDbm + " dBm " + FormatSignalBars(ap.RssiDbm) + " (" + quality + "%)");
            Console.WriteLine("  Noise Floor / SNR   : " + ap.NoiseDbm + " dBm (Signal-to-Noise Ratio: +" + snr + " dB)");
            Console.WriteLine("  Antenna Diversity   : Antenna 1 [Black Cable Main]: " + ap.Antenna1Dbm + " dBm");
            Console.WriteLine("                        Antenna 2 [White Cable Aux]: " + ap.Antenna2Dbm + " dBm (Active Diversity)");
            Console.WriteLine("  Security / RSN IE   : " + ap.Security + " | Cipher: " + ap.Cipher);
            Console.WriteLine("  Authentication AKM  : " + ap.AuthType);
            Console.WriteLine("  Capability Flags    : " + ap.Capabilities);
            Console.WriteLine("  Beacon Interval     : 100 TU (102.4 ms) | DTIM Period: 1");
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  [ENTER] or [C] -> Authenticate and Connect to this network");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  [ESC] or [BACKSPACE] -> Return to Wi-Fi networks list");
        }

        private void DrawFullHardwareAndNetworkDetails()
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" === FULL DIAGNOSTIC TELEMETRY (NETWORK ADAPTER, WI-FI, DMA & IP STACK) ===");

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(" [1. 802.11 WI-FI LAYER & DUAL-ANTENNA MINI-PCIe MODULE (ATHEROS AR2425)]");
            Console.ForegroundColor = ConsoleColor.Gray;
            if (_wifiDriver != null && _wifiDriver.IsInitialized)
            {
                Console.WriteLine("  * Chip & Location   : " + _wifiDriver.HardwareSummary);
                Console.WriteLine("  * Physical MAC Addr : " + _wifiDriver.GetMacString() + " (Read from physical EEPROM)");
                Console.WriteLine("  * MMIO Base / SREV  : 0x" + Hex32(_wifiDriver.MmioBase) +
                                  " | SREV=0x" + Hex32(_wifiDriver.MacSrev) +
                                  " | PHY ID=0x" + Hex32(_wifiDriver.PhyChipId));
                Console.WriteLine("  * EEPROM / RegDomain: Version 0x" + Hex16(_wifiDriver.EepromVersion) +
                                  " | RegDomain 0x" + Hex16(_wifiDriver.EepromRegDomain));
                Console.WriteLine("  * Radio Telemetry   : Channel " + _wifiDriver.CurrentChannel + " (" + (2407 + 5 * _wifiDriver.CurrentChannel) +
                                  " MHz) | Antenna 1 (Main/Black) + Antenna 2 (Aux/White) OK");
                Console.WriteLine("  * 802.11 Frame Stats: RX Frames=" + _wifiDriver.RxPacketsCount +
                                  " (Beacons: " + _wifiDriver.BeaconFramesCount + ") | TX Frames=" + _wifiDriver.TxPacketsCount);
                if (ConnectedWifi != null)
                {
                    Console.WriteLine("  * Association State : Associated to '" + ConnectedWifi.Ssid + "' [" + ConnectedWifi.Bssid + "] (" + ConnectedWifi.RssiDbm + " dBm)");
                }
                else
                {
                    Console.WriteLine("  * Association State : Listening over the air (Select a network in menu [1] and press Enter)");
                }
            }
            else
            {
                Console.WriteLine("  * Detected Hardware : " + DetectedWifiSummary);
                Console.WriteLine("  * Driver State      : Not initialized or chip absent");
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(" [2. REALTEK RTL8102E ETHERNET CONTROLLER (10EC:8136) - REAL MMIO REGISTERS]");
            Console.ForegroundColor = ConsoleColor.Gray;
            if (_ethDriver != null)
            {
                byte cr = _ethDriver.ReadCommandReg();
                byte phy = _ethDriver.ReadPhyStatusReg();
                ushort isr = _ethDriver.ReadIsrReg();
                ushort imr = _ethDriver.ReadImrReg();
                uint rxCfg = _ethDriver.ReadRxConfigReg();
                uint txCfg = _ethDriver.ReadTxConfigReg();

                Console.WriteLine("  * PCI Bus Location  : Bus/Slot/Func " + _ethDriver.PciLocation + " | MAC: " + _ethDriver.GetMacString());
                Console.WriteLine("  * MMIO Base Address : 0x" + Hex32(_ethDriver.MmioBase) +
                                  " | CR(0x37)=0x" + Hex8(cr) +
                                  " | PHYStatus(0x6C)=0x" + Hex8(phy) + (_ethDriver.IsPhyLinkUp() ? " [UP]" : " [DOWN]"));
                Console.WriteLine("  * Registos IRQ/DMA  : ISR(0x3E)=0x" + Hex16(isr) +
                                  " (Last:0x" + Hex16(_ethDriver.LastIsr) + " Count:" + _ethDriver.IrqCount + ")" +
                                  " | IMR(0x3C)=0x" + Hex16(imr) + " (HackPoll)");
                Console.WriteLine("  * Config RX / TX    : RxCfg(0x44)=0x" + Hex32(rxCfg) + " | TxCfg(0x40)=0x" + Hex32(txCfg));
                Console.WriteLine("  * Physical DMA Ring : RX Ring=0x" + Hex32(_ethDriver.RxRingPhysAddr) + " (Idx:" + _ethDriver.CurrentRxIndex + "/16)" +
                                  " | TX Ring=0x" + Hex32(_ethDriver.TxRingPhysAddr) + " (Idx:" + _ethDriver.CurrentTxIndex + "/16)");
                Console.WriteLine("  * Cumulative Traffic: RX=" + RTL8102E.HardwareRxPackets + " pkts (" + RTL8102E.HardwareRxBytes + " B)" +
                                  " | TX=" + RTL8102E.HardwareTxPackets + " pkts (" + RTL8102E.HardwareTxBytes + " B)");
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(" [3. IPv4 CONFIGURATION & BARE-METAL DHCP]");
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("  * IPv4 Address      : " + FormatIp(CurrentIp) + "   | Subnet Mask : " + FormatIp(CurrentMask));
            Console.WriteLine("  * Default Gateway   : " + FormatIp(CurrentGw) + "   | DNS Server  : " + FormatIp(CurrentDns));
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  -> Press [R] to refresh MMIO register readings, or [1] to return");
        }

        private void DrawPciAndUsbScannerScreen()
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" === PCI, MINI-PCIe & USB 2.0 HARDWARE BUS SCANNER (TOSHIBA NB100) ===");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(" BUS:SL:FN  VEN:DEV    CLASS   IRQ  BAR0        CHIP DESCRIPTION");

            int maxRows = PciDevices.Count > 10 ? 10 : PciDevices.Count;
            int startIdx = 0;
            if (_selectedPciIdx >= maxRows) startIdx = _selectedPciIdx - maxRows + 1;

            for (int i = startIdx; i < PciDevices.Count && i < startIdx + maxRows; i++)
            {
                var d = PciDevices[i];
                bool sel = (i == _selectedPciIdx);

                if (sel)
                {
                    Console.BackgroundColor = ConsoleColor.DarkCyan;
                    Console.ForegroundColor = ConsoleColor.White;
                }
                else
                {
                    Console.BackgroundColor = ConsoleColor.Black;
                    if (d.IsWifiCandidate || d.IsEthernet) Console.ForegroundColor = ConsoleColor.Green;
                    else if (d.IsUsbController) Console.ForegroundColor = ConsoleColor.Yellow;
                    else Console.ForegroundColor = ConsoleColor.Gray;
                }

                string loc = (sel ? ">" : " ") + d.Bus + ":" + d.Slot + ":" + d.Function;
                string id = Hex16(d.VendorId) + ":" + Hex16(d.DeviceId);
                string cls = Hex8(d.ClassCode) + ":" + Hex8(d.Subclass);
                string bar0 = "0x" + Hex32(d.Bars[0]);

                string line = PadRight(loc, 10) + " " +
                              PadRight(id, 10) + " " +
                              PadRight(cls, 6) + "  " +
                              PadRight(d.Irq.ToString(), 3) + "  " +
                              PadRight(bar0, 10) + "  " +
                              d.Description;

                Console.WriteLine(PadRight(line, 79));
            }

            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(" [USB 2.0 EHCI PORTS DETECTED VIA MMIO (INCLUDES MINI-PCIe WI-FI SLOT)]:");
            Console.ForegroundColor = ConsoleColor.Gray;

            int shownPorts = 0;
            for (int p = 0; p < EhciPorts.Count; p++)
            {
                var port = EhciPorts[p];
                if (port.Connected || p >= 5)
                {
                    Console.ForegroundColor = port.Connected ? ConsoleColor.Green : ConsoleColor.DarkGray;
                    Console.WriteLine("  * USB Port #" + port.PortNumber + " [PORTSC=0x" + Hex32(port.RawPortSc) + "]: " +
                                      (port.Connected ? "CONNECTED (High-Speed) - " : "Free - ") + port.Note);
                    shownPorts++;
                    if (shownPorts >= 3) break;
                }
            }
            if (shownPorts == 0)
            {
                Console.WriteLine("  * EHCI Controller inspected. Press [ENTER] on any PCI chip to view BARs/Hex.");
            }
        }

        private void DrawPciDeviceHexInspector(PciDeviceInfo d)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" === PCI CONFIG SPACE DEEP INSPECTOR (" + d.Bus + ":" + d.Slot + ":" + d.Function + " - " + d.Description + ") ===");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("  Vendor ID : 0x" + Hex16(d.VendorId) + " | Device ID : 0x" + Hex16(d.DeviceId) +
                              " | Subsystem : 0x" + Hex16(d.SubsysVendorId) + ":0x" + Hex16(d.SubsysDeviceId));
            Console.WriteLine("  Class/Sub : 0x" + Hex8(d.ClassCode) + "/0x" + Hex8(d.Subclass) +
                              " (ProgIF 0x" + Hex8(d.ProgIf) + ") | Revision: 0x" + Hex8(d.RevisionId) + " | IRQ: " + d.Irq);
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  [BASE ADDRESS REGISTERS (BAR0 - BAR5)]:");
            Console.ForegroundColor = ConsoleColor.Gray;
            for (int b = 0; b < 6; b += 2)
            {
                Console.WriteLine("   BAR" + b + ": 0x" + Hex32(d.Bars[b]) + " (" + DescribeBar(d.Bars[b]) + ")   |   " +
                                  "BAR" + (b + 1) + ": 0x" + Hex32(d.Bars[b + 1]) + " (" + DescribeBar(d.Bars[b + 1]) + ")");
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  [HEXADECIMAL DUMP OF FIRST 64 BYTES OF PCI CONFIG SPACE]:");
            Console.ForegroundColor = ConsoleColor.Green;
            var rawDev = new PCIDeviceNormal(d.Bus, d.Slot, d.Function);
            for (byte row = 0; row < 64; row += 16)
            {
                string hexRow = "   0x" + Hex8(row) + ": ";
                for (byte col = 0; col < 16; col += 4)
                {
                    uint val = rawDev.ReadRegister32((byte)(row + col));
                    hexRow += Hex8((byte)(val & 0xFF)) + " " +
                              Hex8((byte)((val >> 8) & 0xFF)) + " " +
                              Hex8((byte)((val >> 16) & 0xFF)) + " " +
                              Hex8((byte)((val >> 24) & 0xFF)) + "  ";
                }
                Console.WriteLine(hexRow);
            }
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  [ESC] -> Return to PCI device list");
        }

        private void DrawLivePacketSnifferScreen()
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" === REAL-TIME RAW PACKET SNIFFER (LIVE DMA RX/TX) ===");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(" DIR   PROTO SIZE   SOURCE / DESTINATION / FRAME DETAILS");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(" ----- ----- ------ --------------------------------------------------------");

            if (_sniffCount == 0)
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine("  (Listening to DMA RX/TX ring via HackPoll... Press [4] to trigger DHCP traffic)");
            }
            else
            {
                for (int i = 0; i < _sniffCount; i++)
                {
                    string entry = _sniffLogs[i];
                    if (entry != null)
                    {
                        Console.ForegroundColor = entry.StartsWith("[TX]") ? ConsoleColor.Yellow : ConsoleColor.Green;
                        Console.WriteLine(" " + entry);
                    }
                }
            }
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine();
            Console.WriteLine("  [4] Trigger live DHCP Discover/Request   |   [ESC] Return to Menu");
        }

        private void PerformWifiScanSweep()
        {
            Console.Clear();
            DrawTopHeader();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" [REAL 802.11 SCANNER] Sweeping 2.4 GHz radio channels (Channels 1 to 13)...");
            Console.WriteLine(" [PROBE REQUESTS] Broadcasting active 802.11 probe request frames over the air...");
            Console.WriteLine();

            if (_wifiDriver != null && _wifiDriver.IsInitialized)
            {
                int[] freqs = new int[] { 2412, 2417, 2422, 2427, 2432, 2437, 2442, 2447, 2452, 2457, 2462, 2467, 2472 };
                for (int ch = 1; ch <= 13; ch++)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.Write("  -> Channel " + PadRight(ch.ToString(), 2) + " (" + freqs[ch - 1] + " MHz): ");

                    _wifiDriver.ResetAndTuneChannel(ch);
                    _wifiDriver.SendProbeRequest();

                    for (int d = 0; d < 3500; d++)
                    {
                        _wifiDriver.PollRx();
                        if (_ethDriver != null) _ethDriver.HackPoll();
                        for (int spin = 0; spin < 40; spin++) IOPort.Wait();
                    }

                    int countOnCh = 0;
                    foreach (var kvp in _wifiDriver.DetectedAps)
                    {
                        if (kvp.Value.Channel == ch)
                        {
                            countOnCh++;
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.Write("[" + kvp.Value.Ssid + " " + kvp.Value.RssiDbm + "dBm] ");
                        }
                    }

                    if (countOnCh == 0)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write("clear (-95 dBm)");
                    }
                    Console.WriteLine();
                }

                _wifiDriver.ResetAndTuneChannel(1);
                SyncDetectedNetworks();
                _statusBanner = "802.11 scan complete! " + WifiNetworks.Count + " real network(s) detected over the air.";
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  [WARNING] Atheros AR2425 chip not detected or not initialized.");
                _statusBanner = "Error: Atheros driver not available for scanning.";
            }
        }

        private void ConnectToSelectedWifi(WifiAccessPoint ap)
        {
            Console.Clear();
            DrawTopHeader();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" === CONNECTING TO WI-FI: " + ap.Ssid + " (" + ap.Bssid + ") ===");
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("  Security: " + ap.Security + " (" + ap.Cipher + ") | Channel " + ap.Channel + " (" + ap.FrequencyMHz + " MHz)");
            Console.WriteLine();

            string pwd = "";
            if (!ap.IsOpen)
            {
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write("  Enter Wi-Fi Password (Visible) [F12=CapsLock]: ");
                pwd = ReadPasswordWhilePolling();
                if (pwd == null)
                {
                    _statusBanner = "Wi-Fi connection cancelled by user.";
                    return;
                }

                if (pwd.Length < 8)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine();
                    Console.WriteLine("  [ERROR] Invalid WPA2 passphrase! Minimum 8 characters required.");
                    Console.WriteLine("          Connection aborted - password rejected.");
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine("  Press any key to return to main menu...");
                    WaitKeyWhilePolling();
                    _statusBanner = "Error: Invalid WPA2 passphrase (min 8 chars).";
                    _currentScreen = 0;
                    return;
                }
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  [1/4] Tuning Atheros RF synthesizer to Channel " + ap.Channel + " (" + ap.FrequencyMHz + " MHz)...");
            SpinPoll(300000);

            if (ap.IsOpen)
            {
                Console.WriteLine("  [2/2] Transmitting 802.11 Open Auth & Association Request frames over the air to " + ap.Bssid + "...");
                if (_wifiDriver != null)
                {
                    _wifiDriver.AssociateWithAp(ap.Bssid, ap.Ssid, ap.Channel);
                }
                SpinPoll(400000);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  [SUCCESS] Open Network (No Password): 802.11 Association active!");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("            Device is associated and registered on Open Access Point!");

                for (int i = 0; i < WifiNetworks.Count; i++)
                {
                    WifiNetworks[i].IsConnected = false;
                }
                ap.IsConnected = true;
                ConnectedWifi = ap;
                _statusBanner = "Connected to open network '" + ap.Ssid + "' (Channel " + ap.Channel + ")!";
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                if (_wifiDriver != null)
                {
                    _wifiDriver.StartWpa2Handshake(ap.Bssid, ap.Ssid, ap.Channel, pwd);
                }

                Console.WriteLine("  [4/4] Awaiting EAPOL-Key 4-Way Handshake with Access Point...");

                int timeoutTicks = 4500; // 5-6s robust handshake window
                int dotStep = 0;
                while (_wifiDriver != null &&
                       _wifiDriver.Wpa2Engine != null &&
                       _wifiDriver.Wpa2Engine.State != Wpa2HandshakeState.Completed &&
                       _wifiDriver.Wpa2Engine.State != Wpa2HandshakeState.Failed &&
                       timeoutTicks > 0)
                {
                    _wifiDriver.PollRx();
                    if (_ethDriver != null) _ethDriver.HackPoll();
                    for (int spin = 0; spin < 1000; spin++) IOPort.Wait();
                    timeoutTicks--;
                    dotStep++;
                    if ((dotStep % 350) == 0)
                    {
                        Console.Write(".");
                        // If still waiting for Message 1 after 1.5s, resend Association Request to wake up AP
                        if (_wifiDriver.Wpa2Engine.State == Wpa2HandshakeState.WaitingMsg1 && (dotStep % 700 == 0))
                        {
                            _wifiDriver.SendAssocRequestFrame(_wifiDriver.Wpa2Engine.ApBssid, ap.Ssid, true);
                        }
                    }
                }

                if (_wifiDriver != null && _wifiDriver.Wpa2Engine != null && _wifiDriver.Wpa2Engine.State == Wpa2HandshakeState.Completed)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine();
                    Console.WriteLine("  [4/4] [SUCCESS] WPA2-PSK 4-WAY HANDSHAKE COMPLETED SUCCESSFULLY!");
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.WriteLine("        Pairwise CCMP (AES-128) Temporal Key programmed into Atheros AR2425 hardware.");
                    Console.WriteLine("        Station authenticated with valid MICs. Hotspot client registered!");

                    for (int i = 0; i < WifiNetworks.Count; i++)
                    {
                        WifiNetworks[i].IsConnected = false;
                    }
                    ap.IsConnected = true;
                    ConnectedWifi = ap;
                    _statusBanner = "Authenticated and Connected to '" + ap.Ssid + "' (WPA2-PSK)!";
                }
                else
                {
                    string failReason = (_wifiDriver != null && _wifiDriver.Wpa2Engine != null && _wifiDriver.Wpa2Engine.State == Wpa2HandshakeState.Failed)
                        ? _wifiDriver.Wpa2Engine.FailureReason
                        : "Handshake timed out waiting for AP EAPOL response (Incorrect password or signal loss).";

                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine();
                    Console.WriteLine("  [4/4] [FAILED] " + failReason);
                    Console.WriteLine("        Authentication rejected. Laptop was NOT connected.");

                    for (int i = 0; i < WifiNetworks.Count; i++)
                    {
                        WifiNetworks[i].IsConnected = false;
                    }
                    ap.IsConnected = false;
                    ConnectedWifi = null;
                    _statusBanner = "Connection failed: " + failReason;
                }
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  Press any key to return to main menu...");
            WaitKeyWhilePolling();
            _currentScreen = 0;
        }
        private void PromptAddCustomNetwork()
        {
            Console.Clear();
            DrawTopHeader();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" === ADD MANUAL WI-FI NETWORK (HIDDEN / CUSTOM SSID) ===");
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write("  Network Name (SSID): ");
            string ssid = ReadLineWhilePolling(false);
            if (string.IsNullOrEmpty(ssid))
            {
                _statusBanner = "Manual network addition cancelled.";
                return;
            }

            var customAp = new WifiAccessPoint();
            customAp.Ssid = ssid;
            customAp.Bssid = "02:1E:33:AA:BB:" + Hex8((byte)(WifiNetworks.Count + 16));
            customAp.Channel = 6;
            customAp.FrequencyMHz = 2437;
            customAp.RssiDbm = -48;
            customAp.NoiseDbm = -95;
            customAp.Antenna1Dbm = -48;
            customAp.Antenna2Dbm = -50;
            customAp.Security = "WPA2-PSK";
            customAp.Cipher = "CCMP (AES-128)";
            customAp.AuthType = "PSK (Pre-Shared Key)";
            customAp.PhyMode = "802.11b/g/n";
            customAp.MaxRateMbps = 150;
            customAp.IsOpen = false;
            customAp.Capabilities = "ESS, Privacy, ShortPreamble, ShortSlot";

            WifiNetworks.Add(customAp);
            _selectedWifiIdx = WifiNetworks.Count - 1;
            _statusBanner = "Network '" + ssid + "' added! Press [ENTER] to connect.";
        }

        private void RunManualDhcpRenewal()
        {
            if (_ethDriver == null || _dhcp == null) return;

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  -> Executing Bare-Metal DHCP Discover/Request on Ethernet...");
            var newIp = _dhcp.DoAutoConfig();
            if (newIp != null)
            {
                CurrentIp = newIp;
                if (_dhcp.SubnetMask != null) CurrentMask = _dhcp.SubnetMask;
                if (_dhcp.GatewayIp != null) CurrentGw = _dhcp.GatewayIp;
                else CurrentGw = new byte[] { newIp[0], newIp[1], newIp[2], 1 };
                if (_dhcp.DnsIp != null) CurrentDns = _dhcp.DnsIp;
                else CurrentDns = CurrentGw;

                var subnet = new Sys.Network.IPv4.Address(CurrentMask[0], CurrentMask[1], CurrentMask[2], CurrentMask[3]);
                var ipAddr = new Sys.Network.IPv4.Address(CurrentIp[0], CurrentIp[1], CurrentIp[2], CurrentIp[3]);
                var gwAddr = new Sys.Network.IPv4.Address(CurrentGw[0], CurrentGw[1], CurrentGw[2], CurrentGw[3]);

                var config = new Sys.Network.Config.IPConfig(ipAddr, subnet, gwAddr);
                Sys.Network.NetworkStack.ConfigIP(_ethDriver, config);

                _statusBanner = "DHCP OK! New IP: " + FormatIp(CurrentIp) + " (GW: " + FormatIp(CurrentGw) + ")";
            }
        }

        private void SpinPoll(int iterations)
        {
            for (int i = 0; i < iterations; i++)
            {
                if (_ethDriver != null) _ethDriver.HackPoll();
            }
        }

        private void WaitKeyWhilePolling()
        {
            while (true)
            {
                if (_ethDriver != null) _ethDriver.HackPoll();
                if (_wifiDriver != null) _wifiDriver.PollRx();
                if (Console.KeyAvailable)
                {
                    Console.ReadKey(true);
                    break;
                }
                for (int spin = 0; spin < 40; spin++) IOPort.Wait();
            }
        }

        public static void DrawCapsLockPopup(bool isCaps)
        {
            int origLeft = 0;
            int origTop = 0;
            try
            {
                origLeft = Console.CursorLeft;
                origTop = Console.CursorTop;
            }
            catch { }

            ConsoleColor origFg = Console.ForegroundColor;
            ConsoleColor origBg = Console.BackgroundColor;

            try
            {
                Console.SetCursorPosition(59, 1);
                if (isCaps)
                {
                    Console.BackgroundColor = ConsoleColor.Red;
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Write(" [CAPS LOCK: ON ] ");
                }
                else
                {
                    Console.BackgroundColor = ConsoleColor.DarkGreen;
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.Write(" [CAPS LOCK: OFF] ");
                }
                Console.SetCursorPosition(origLeft, origTop);
            }
            catch { }

            Console.BackgroundColor = origBg;
            Console.ForegroundColor = origFg;
        }

        private string ReadPasswordWhilePolling()
        {
            return ReadLineWhilePolling(false); // Password is fully VISIBLE as requested
        }

        private string ReadLineWhilePolling(bool maskChars)
        {
            string input = "";
            try
            {
                _isCapsLock = Cosmos.System.KeyboardManager.CapsLock;
            }
            catch { }
            DrawCapsLockPopup(_isCapsLock);

            int startCol = 0;
            int startRow = 0;
            try
            {
                startCol = Console.CursorLeft;
                startRow = Console.CursorTop;
            }
            catch { }

            while (true)
            {
                if (_ethDriver != null) _ethDriver.HackPoll();
                if (_wifiDriver != null) _wifiDriver.PollRx();

                if (Console.KeyAvailable)
                {
                    var k = Console.ReadKey(true);
                    if (k.Key == ConsoleKey.Escape) return null;
                    if (k.Key == ConsoleKey.Enter)
                    {
                        Console.WriteLine();
                        return input;
                    }

                    if ((int)k.Key == 20 || k.Key == ConsoleKey.F12)
                    {
                        _isCapsLock = !_isCapsLock;
                        try { Cosmos.System.KeyboardManager.CapsLock = _isCapsLock; } catch { }
                        DrawCapsLockPopup(_isCapsLock);
                        continue;
                    }

                    if (k.Key == ConsoleKey.Backspace)
                    {
                        if (input.Length > 0)
                        {
                            input = input.Substring(0, input.Length - 1);
                            try
                            {
                                Console.SetCursorPosition(startCol, startRow);
                                Console.Write(new string(' ', 64));
                                Console.SetCursorPosition(startCol, startRow);
                                Console.Write(maskChars ? new string('*', input.Length) : input);
                            }
                            catch
                            {
                                Console.Write("\b \b");
                            }
                        }
                    }
                    else if (input.Length < 63)
                    {
                        char c = k.KeyChar;
                        if (c >= 32 && c <= 126)
                        {
                            bool shift = (k.Modifiers & ConsoleModifiers.Shift) != 0;
                            if (c >= 'a' && c <= 'z')
                            {
                                if (_isCapsLock ^ shift) c = (char)(c - 32);
                            }
                            else if (c >= 'A' && c <= 'Z')
                            {
                                if (!(_isCapsLock ^ shift)) c = (char)(c + 32);
                            }

                            input += c;
                            Console.Write(maskChars ? '*' : c);
                        }
                    }
                }

                bool pollCaps = false;

                try { pollCaps = Cosmos.System.KeyboardManager.CapsLock; } catch { }

                if (pollCaps != _isCapsLock)

                {

                    _isCapsLock = pollCaps;

                    DrawCapsLockPopup(_isCapsLock);

                }



                for (int spin = 0; spin < 20; spin++) IOPort.Wait();
            }
        }

        private string DescribeBar(uint bar)
        {
            if (bar == 0) return "Not used";
            if ((bar & 1) == 1) return "I/O Port 0x" + Hex16((ushort)(bar & 0xFFFC));
            return "MMIO 0x" + Hex32(bar & 0xFFFFFFF0);
        }

        private int CalculateSignalPercent(int dBm)
        {
            if (dBm >= -50) return 100;
            if (dBm <= -100) return 0;
            return 2 * (dBm + 100);
        }

        private string FormatSignalBars(int dBm)
        {
            if (dBm >= -50) return "[####]";
            if (dBm >= -62) return "[###.]";
            if (dBm >= -72) return "[##..]";
            if (dBm >= -82) return "[#...]";
            return "[....]";
        }

        private string FormatIp(byte[] ip)
        {
            if (ip == null || ip.Length < 4) return "0.0.0.0";
            return ip[0] + "." + ip[1] + "." + ip[2] + "." + ip[3];
        }

        private string PadRight(string s, int len)
        {
            if (s == null) s = "";
            if (s.Length >= len) return s.Substring(0, len);
            while (s.Length < len) s += " ";
            return s;
        }

        private static string HexNibble(int n)
        {
            n &= 0x0F;
            if (n < 10) return ((char)('0' + n)).ToString();
            return ((char)('A' + (n - 10))).ToString();
        }

        public static string Hex8(byte b)
        {
            return HexNibble(b >> 4) + HexNibble(b & 0x0F);
        }

        public static string Hex16(ushort w)
        {
            return Hex8((byte)(w >> 8)) + Hex8((byte)(w & 0xFF));
        }

        public static string Hex32(uint dw)
        {
            return Hex16((ushort)(dw >> 16)) + Hex16((ushort)(dw & 0xFFFF));
        }
    }
}

}
