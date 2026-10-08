using System;
using Cosmos.Core;
using Cosmos.HAL;

namespace RTL8102E_Driver
{
    public class RTL8102E
    {
        private PCIDeviceNormal pciDevice;
        private uint mmioBase;
        private MemoryBlock mmio;
        public byte[] MACAddress { get; private set; } = new byte[6];

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
                MACAddress[i] = mmio.Bytes[i];
            }

            Console.Write("r8102e: mac address ");
            for (int i = 0; i < 6; i++)
            {
                Console.Write(MACAddress[i].ToString("x2"));
                if (i < 5) Console.Write(":");
            }
            Console.WriteLine();

            Console.WriteLine("r8102e: phase 1 and 2 complete");
            return true;
        }
    }
}
