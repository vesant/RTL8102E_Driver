using System;
using System.Collections.Generic;
using System.Text;
using Sys = Cosmos.System;

namespace RTL8102E_Driver {
    public class Kernel : Sys.Kernel {

        protected override void BeforeRun() {
            Console.WriteLine("Cosmos kernel ready.");
            Console.WriteLine("loading r8102e driver module...");
            
            var driver = new RTL8102E();
            bool result = driver.Initialize();
            
            if (result)
            {
                Console.WriteLine("r8102e: init success. waiting for link...");
            }
            else
            {
                Console.WriteLine("r8102e: probe failed.");
            }
        }

        protected override void Run() {
            while(true) { }
        }
    }
}
