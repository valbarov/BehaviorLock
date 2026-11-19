using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace BehaviorLock.Tests;

/// <summary>A host environment that only has a content root, for tests that create library classes directly.</summary>
internal sealed class ContentRoot(string path) : IHostEnvironment
{
    public string ApplicationName { get; set; } = "BehaviorLock.Tests";

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

    public string ContentRootPath { get; set; } = path;

    public string EnvironmentName { get; set; } = Environments.Production;
}
