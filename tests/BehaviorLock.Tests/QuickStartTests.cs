using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BehaviorLock.Tests;

/// <summary>
/// The recording test of the quick start in the README, with the sample API as the app. It uses the plain
/// <see cref="WebApplicationFactory{TEntryPoint}"/>, turns Learn mode on and sends a legal request.
/// The profile must then be in the project folder of the app, where it can be committed.
/// </summary>
public sealed class QuickStartTests
{
    [Fact]
    public async Task The_recording_test_writes_the_profile_into_the_project_folder_of_the_app()
    {
        // The quick start uses the default name, behavior.lock.json. That is the committed profile of the
        // sample, so this test writes a file with a name of its own and deletes it.
        var name = $"quick-start.{Guid.NewGuid():N}.lock.json";
        string path;

        // The sample needs a database. A shared in-memory database lives as long as one connection is open.
        var connectionString = $"Data Source=quick-start-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        using var keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();

        await using (var app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Shop", connectionString);
            builder.ConfigureServices(services => services.Configure<BehaviorLockOptions>(options =>
            {
                options.Mode = GuardMode.Learn;
                options.ProfilePath = name;
            }));
        }))
        {
            path = Path.Combine(app.Services.GetRequiredService<IHostEnvironment>().ContentRootPath, name);

            using var client = app.CreateClient();
            using var response = await client.GetAsync("/products/1");
            Assert.True(response.IsSuccessStatusCode);

            // The end of this block stops the app, and that saves the profile.
        }

        try
        {
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "SampleApi.csproj")), path);
            Assert.True(ProfileFile.Read(path)?.ContainsEndpoint("GET /products/{id:int}"), path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
