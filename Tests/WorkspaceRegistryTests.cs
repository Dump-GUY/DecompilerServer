using System.Text.Json;
using DecompilerServer;
using DecompilerServer.Services;
using Microsoft.Extensions.Logging;

namespace Tests;

public class WorkspaceRegistryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _registryPath;

    public WorkspaceRegistryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"DecompilerWorkspaceRegistry_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _registryPath = Path.Combine(_tempDir, "contexts.json");
    }

    [Fact]
    public void LoadAssembly_PersistsAliasRegistration()
    {
        using var workspace = new DecompilerWorkspace(_registryPath);

        workspace.LoadAssembly(new WorkspaceLoadRequest
        {
            AssemblyPath = TestAssemblyLocator.GetPath(),
            ContextAlias = "rw14",
            RebuildIndex = false,
            MakeCurrent = true
        });

        Assert.True(File.Exists(_registryPath));

        var json = File.ReadAllText(_registryPath);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("rw14", root.GetProperty("currentContextAlias").GetString());
        Assert.Equal(1, root.GetProperty("contexts").GetArrayLength());
        Assert.Equal("rw14", root.GetProperty("contexts")[0].GetProperty("contextAlias").GetString());
        Assert.Equal(TestAssemblyLocator.GetPath(), root.GetProperty("contexts")[0].GetProperty("assemblyPath").GetString());
    }

    [Fact]
    public void RegisteredContexts_LoadOnlyWhenAddressed()
    {
        using (var firstWorkspace = new DecompilerWorkspace(_registryPath))
        {
            firstWorkspace.LoadAssembly(new WorkspaceLoadRequest
            {
                AssemblyPath = TestAssemblyLocator.GetPath(),
                ContextAlias = "rw14",
                RebuildIndex = false,
                MakeCurrent = true
            });

            firstWorkspace.LoadAssembly(new WorkspaceLoadRequest
            {
                AssemblyPath = typeof(global::EmbeddedSourceTestLibrary.EmbeddedSourceSample).Assembly.Location,
                ContextAlias = "rw15",
                RebuildIndex = false,
                MakeCurrent = false
            });
        }

        using var restartedWorkspace = new DecompilerWorkspace(_registryPath);

        Assert.Equal("rw14", restartedWorkspace.CurrentContextAlias);
        Assert.Equal(["rw14", "rw15"], restartedWorkspace.ListRegisteredAliases());
        Assert.Empty(restartedWorkspace.ListContexts());
        Assert.False(restartedWorkspace.TryAcquireCurrentLoadedSession(out _));

        using var rw15Lease = restartedWorkspace.AcquireSession("rw15");
        var rw15 = rw15Lease.Session;

        Assert.Equal("rw15", rw15.ContextAlias);
        Assert.Equal("rw14", restartedWorkspace.CurrentContextAlias);
        Assert.Single(restartedWorkspace.ListContexts());

        using var currentLease = restartedWorkspace.AcquireCurrentSession();
        var current = currentLease.Session;

        Assert.Equal("rw14", current.ContextAlias);
        Assert.Equal(2, restartedWorkspace.ListContexts().Count);
    }

    [Fact]
    public void UnloadContext_PersistsUpdatedCurrentSelection()
    {
        using var workspace = new DecompilerWorkspace(_registryPath);

        workspace.LoadAssembly(new WorkspaceLoadRequest
        {
            AssemblyPath = TestAssemblyLocator.GetPath(),
            ContextAlias = "rw14",
            RebuildIndex = false,
            MakeCurrent = true
        });

        workspace.LoadAssembly(new WorkspaceLoadRequest
        {
            AssemblyPath = typeof(global::EmbeddedSourceTestLibrary.EmbeddedSourceSample).Assembly.Location,
            ContextAlias = "rw15",
            RebuildIndex = false,
            MakeCurrent = false
        });

        workspace.UnloadContext("rw14");

        var json = File.ReadAllText(_registryPath);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("rw15", workspace.CurrentContextAlias);
        Assert.Equal("rw15", root.GetProperty("currentContextAlias").GetString());
        Assert.DoesNotContain(root.GetProperty("contexts").EnumerateArray(), item =>
            item.GetProperty("contextAlias").GetString() == "rw14");
    }

    [Fact]
    public void UnloadContext_WithPreserveRegistration_KeepsAliasRegistered()
    {
        using var workspace = new DecompilerWorkspace(_registryPath);

        workspace.LoadAssembly(new WorkspaceLoadRequest
        {
            AssemblyPath = TestAssemblyLocator.GetPath(),
            ContextAlias = "rw14",
            RebuildIndex = false,
            MakeCurrent = true
        });

        workspace.UnloadContext("rw14", preserveRegistration: true);

        var json = File.ReadAllText(_registryPath);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Contains(root.GetProperty("contexts").EnumerateArray(), item =>
            item.GetProperty("contextAlias").GetString() == "rw14");
    }

    [Fact]
    public void UnloadContext_RemovesDeferredRegistrationWithoutLoadingIt()
    {
        using (var firstWorkspace = new DecompilerWorkspace(_registryPath))
        {
            firstWorkspace.LoadAssembly(new WorkspaceLoadRequest
            {
                AssemblyPath = TestAssemblyLocator.GetPath(),
                ContextAlias = "rw14",
                RebuildIndex = false,
                MakeCurrent = true
            });

            firstWorkspace.LoadAssembly(new WorkspaceLoadRequest
            {
                AssemblyPath = typeof(global::EmbeddedSourceTestLibrary.EmbeddedSourceSample).Assembly.Location,
                ContextAlias = "rw15",
                RebuildIndex = false,
                MakeCurrent = false
            });
        }

        using var restartedWorkspace = new DecompilerWorkspace(_registryPath);
        Assert.Empty(restartedWorkspace.ListContexts());

        restartedWorkspace.UnloadContext("rw15");

        Assert.Empty(restartedWorkspace.ListContexts());
        Assert.Equal(["rw14"], restartedWorkspace.ListRegisteredAliases());
    }

    [Fact]
    public void DuplicateMvidContext_DoesNotStealOrEraseCanonicalMemberRouting()
    {
        using var workspace = new DecompilerWorkspace(_registryPath);
        var primaryPath = TestAssemblyLocator.GetPath();
        var otherPath = typeof(global::EmbeddedSourceTestLibrary.EmbeddedSourceSample).Assembly.Location;

        workspace.LoadAssembly(new WorkspaceLoadRequest
        {
            AssemblyPath = primaryPath,
            ContextAlias = "primary",
            RebuildIndex = false,
            MakeCurrent = true
        });

        using var primaryLease = workspace.AcquireCurrentSession();
        var primarySession = primaryLease.Session;
        var type = primarySession.ContextManager.FindTypeByName("TestLibrary.SimpleClass");
        Assert.NotNull(type);
        var memberId = primarySession.MemberResolver.GenerateMemberId(type);

        workspace.LoadAssembly(new WorkspaceLoadRequest
        {
            AssemblyPath = primaryPath,
            ContextAlias = "duplicate",
            RebuildIndex = false,
            MakeCurrent = false
        });

        workspace.LoadAssembly(new WorkspaceLoadRequest
        {
            AssemblyPath = otherPath,
            ContextAlias = "other",
            RebuildIndex = false,
            MakeCurrent = true
        });

        using (var routedLease = workspace.AcquireSessionForMemberId(memberId))
        {
            Assert.Equal("primary", routedLease.Session.ContextAlias);
        }

        workspace.UnloadContext("duplicate");

        Assert.Equal("other", workspace.CurrentContextAlias);
        using var reroutedLease = workspace.AcquireSessionForMemberId(memberId);
        Assert.Equal("primary", reroutedLease.Session.ContextAlias);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch
            {
                // Ignore cleanup errors.
            }
        }
    }

    [Fact]
    public async Task WorkspaceBootstrapService_LogsRegisteredAliasesOnStartup()
    {
        using (var firstWorkspace = new DecompilerWorkspace(_registryPath))
        {
            firstWorkspace.LoadAssembly(new WorkspaceLoadRequest
            {
                AssemblyPath = TestAssemblyLocator.GetPath(),
                ContextAlias = "rw14",
                RebuildIndex = false,
                MakeCurrent = true
            });
        }

        using var restartedWorkspace = new DecompilerWorkspace(_registryPath);
        var logger = new ListLogger<WorkspaceBootstrapService>();
        var bootstrapService = new WorkspaceBootstrapService(logger, restartedWorkspace);

        await bootstrapService.StartAsync(CancellationToken.None);
        await bootstrapService.StopAsync(CancellationToken.None);

        Assert.Empty(restartedWorkspace.ListContexts());
        Assert.Contains(logger.Messages, message =>
            message.Contains("1 workspace aliases", StringComparison.Ordinal)
            && message.Contains("rw14", StringComparison.Ordinal));
    }
}

internal sealed class ListLogger<T> : ILogger<T>, IDisposable
{
    public List<string> Messages { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => this;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Messages.Add(formatter(state, exception));
    }

    public void Dispose()
    {
    }
}
