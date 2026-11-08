using System;
using System.Collections.Generic;
using System.Text;
using Sys = Cosmos.System;

namespace RTL8102E_Driver {
    public class Kernel : Sys.Kernel {
        private RTL8102E driver;
        private AtherosAR2425Driver wifiDriver;
        private CustomDHCP dhcp;
        private NetworkMenuManager menu;

        protected override void BeforeRun() {
            Console.WriteLine("Cosmos kernel ready.");
            Console.WriteLine("Loading RTL8102E Fast Ethernet + Atheros AR2425 Dual-Antenna Wi-Fi modules...");
            
            driver = new RTL8102E();
            bool result = driver.Initialize();
            byte[] assignedIp = null;
            byte[] mask = new byte[] { 255, 255, 255, 0 };
            byte[] gw = null;
            byte[] dns = null;
            
            if (result)
            {
                Console.WriteLine("r8102e: Driver ready! Initiating bare-metal DHCP negotiation...");
                
                dhcp = new CustomDHCP(driver);
                assignedIp = dhcp.DoAutoConfig();

                if (assignedIp != null)
                {
                    // Bind the newly grabbed IP and Gateway to the Cosmos NetworkStack
                    mask = dhcp.SubnetMask ?? new byte[] { 255, 255, 255, 0 };
                    gw = dhcp.GatewayIp ?? new byte[] { assignedIp[0], assignedIp[1], assignedIp[2], 1 };
                    dns = dhcp.DnsIp ?? gw;

                    var subnet = new Sys.Network.IPv4.Address(mask[0], mask[1], mask[2], mask[3]);
                    var ipAddr = new Sys.Network.IPv4.Address(assignedIp[0], assignedIp[1], assignedIp[2], assignedIp[3]);
                    var gwAddr = new Sys.Network.IPv4.Address(gw[0], gw[1], gw[2], gw[3]);
                    
                    var config = new Sys.Network.Config.IPConfig(ipAddr, subnet, gwAddr);
                    Sys.Network.NetworkStack.ConfigIP(driver, config);
                    
                    Console.WriteLine($"r8102e: NetworkStack successfully bound to {assignedIp[0]}.{assignedIp[1]}.{assignedIp[2]}.{assignedIp[3]} (Gateway: {gw[0]}.{gw[1]}.{gw[2]}.{gw[3]})!");
                }
                else
                {
                    Console.WriteLine("r8102e: DHCP failed. Unable to configure NetworkStack.");
                }
            }
            else
            {
                Console.WriteLine("r8102e: PCI probe failed.");
            }

            Console.WriteLine("ath5k: Initializing Atheros AR2425 802.11b/g bare-metal wireless driver...");
            wifiDriver = new AtherosAR2425Driver();
            bool wifiResult = wifiDriver.FindAndInitialize();
            if (wifiResult)
            {
                Console.WriteLine("ath5k: Atheros AR2425 wireless radio initialized and calibrated!");
                Console.WriteLine("ath5k: Performing initial 802.11 RF channel sweep (Ch 1..13)...");
                wifiDriver.ScanAllChannels();
            }
            else
            {
                Console.WriteLine("ath5k: Atheros AR2425 adapter not detected on PCI bus.");
            }

            Console.WriteLine("Loading Interactive Wi-Fi & Hardware Inspector Menu...");
            menu = new NetworkMenuManager(driver, wifiDriver, dhcp, assignedIp, mask, gw, dns);
        }

        protected override void Run() {
            if (menu != null)
            {
                menu.UpdateStep();
            }
            else if (driver != null)
            {
                driver.HackPoll();
                Sys.Network.NetworkStack.Update();
            }
        }
    }
}
