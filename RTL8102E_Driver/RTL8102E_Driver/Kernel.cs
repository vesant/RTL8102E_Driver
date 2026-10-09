using System;
using System.Collections.Generic;
using System.Text;
using Sys = Cosmos.System;

namespace RTL8102E_Driver {
    public class Kernel : Sys.Kernel {
        private RTL8102E driver;
        private uint lastIrqCount = 0;

        protected override void BeforeRun() {
            Console.WriteLine("Cosmos kernel ready.");
            Console.WriteLine("loading r8102e driver module...");
            
            driver = new RTL8102E();
            bool result = driver.Initialize();
            
            if (result)
            {
                Console.WriteLine("r8102e: init success. configuring NetworkStack...");
                
                // Set static IP to 192.168.31.100 just for testing (matching user subnet)
                var ip = new Sys.Network.IPv4.Address(192, 168, 31, 100);
                var subnet = new Sys.Network.IPv4.Address(255, 255, 255, 0);
                var gw = new Sys.Network.IPv4.Address(192, 168, 31, 1);
                var config = new Sys.Network.Config.IPConfig(ip, subnet, gw);
                Sys.Network.NetworkStack.ConfigIP(driver, config);

                Console.WriteLine("r8102e: NetworkStack bound successfully to 192.168.31.100!");
            }
            else
            {
                Console.WriteLine("r8102e: probe failed.");
            }
        }

        protected override void Run() {
            if (driver != null)
            {
                Sys.Network.NetworkStack.Update();
            }
        }
    }
}
