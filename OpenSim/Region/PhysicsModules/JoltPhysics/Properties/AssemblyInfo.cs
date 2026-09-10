using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mono.Addins;

[assembly: AssemblyTitle("OpenSim.Region.PhysicsModule.JoltPhysics")]
[assembly: AssemblyDescription("Jolt physics region module")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("http://opensimulator.org")]
[assembly: AssemblyProduct("OpenSim")]
[assembly: AssemblyCopyright("OpenSimulator developers")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]

[assembly: AssemblyVersion(OpenSim.VersionInfo.AssemblyVersionNumber)]

// Mono.Addins discovery: this assembly IS an addin, dependent on the region framework. The
// [Extension] attribute on JoltPhysicsScene registers it as a RegionModule; it self-selects on
// [Startup] physics = Jolt.
[assembly: Addin("OpenSim.Region.PhysicsModule.JoltPhysics", OpenSim.VersionInfo.AssemblyVersionNumber)]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.AssemblyVersionNumber)]
