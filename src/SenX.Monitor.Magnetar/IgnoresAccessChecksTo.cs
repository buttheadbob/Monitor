// Hub review expects this in source: a build that compiles the plugin itself would not get
// the copy the publicizer can inject through MSBuild.
using System.Runtime.CompilerServices;

[assembly: IgnoresAccessChecksTo("Sandbox.Game")]

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class IgnoresAccessChecksToAttribute : Attribute
    {
        public IgnoresAccessChecksToAttribute(string assemblyName) => AssemblyName = assemblyName;

        public string AssemblyName { get; }
    }
}
