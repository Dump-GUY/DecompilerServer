using System.Text.RegularExpressions;
using System.Text.Json;

namespace DecompilerServer.Services;

public sealed class DecompilerWorkspace : IDisposable
{
    private const string DefaultContextAlias = "default";
    private static readonly JsonSerializerOptions RegistrySerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly ReaderWriterLockSlim _lock = new();
    private readonly object _activationLock = new();
    private readonly Dictionary<string, DecompilerSession> _sessionsByAlias = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _aliasByMvid = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _registryPath;
    private WorkspaceRegistryState _registryState;
    private bool _disposed;

    public string? CurrentContextAlias { get; private set; }

    public DecompilerWorkspace(string? registryPath = null)
    {
        _registryPath = registryPath ?? GetDefaultRegistryPath();
        _registryState = LoadRegistryState(_registryPath);
        CurrentContextAlias = _registryState.Contexts.Any(entry =>
            string.Equals(entry.ContextAlias, _registryState.CurrentContextAlias, StringComparison.OrdinalIgnoreCase))
            ? _registryState.CurrentContextAlias
            : null;
    }

    public WorkspaceContextInfo LoadAssembly(WorkspaceLoadRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ValidateRequest(request);

        var contextAlias = NormalizeAlias(request.ContextAlias);
        var session = DecompilerSession.Create(contextAlias, request);

        _lock.EnterWriteLock();
        try
        {
            if (_sessionsByAlias.TryGetValue(contextAlias, out var existing))
            {
                RemoveSessionMappings(existing);
                existing.Dispose();
            }

            _sessionsByAlias[contextAlias] = session;
            AddSessionMappings(session);

            if (request.MakeCurrent || CurrentContextAlias == null)
            {
                CurrentContextAlias = contextAlias;
            }

            if (request.PersistRegistration)
            {
                UpsertRegistryEntry(contextAlias, request);
                SaveRegistryState();
            }

            return session.ToContextInfo(isCurrent: string.Equals(CurrentContextAlias, contextAlias, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            session.Dispose();
            throw;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public WorkspaceContextInfo SelectContext(string contextAlias)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(contextAlias))
            throw new ArgumentException("Context alias cannot be empty.", nameof(contextAlias));

        var session = GetOrLoadSession(contextAlias);

        _lock.EnterWriteLock();
        try
        {
            CurrentContextAlias = session.ContextAlias;
            SaveRegistryState();
            return session.ToContextInfo(isCurrent: true);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public IReadOnlyList<WorkspaceContextInfo> ListContexts()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _lock.EnterReadLock();
        try
        {
            return _sessionsByAlias.Values
                .Select(session => session.ToContextInfo(isCurrent: string.Equals(CurrentContextAlias, session.ContextAlias, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(info => info.ContextAlias, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public bool TryGetCurrentSession(out DecompilerSession session)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _lock.EnterReadLock();
        try
        {
            if (CurrentContextAlias != null && _sessionsByAlias.TryGetValue(CurrentContextAlias, out session!))
                return true;

            session = null!;
            return false;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public DecompilerSession GetCurrentSession()
    {
        if (TryGetCurrentSession(out var session))
            return session;

        string? currentContextAlias;
        _lock.EnterReadLock();
        try
        {
            currentContextAlias = CurrentContextAlias;
        }
        finally
        {
            _lock.ExitReadLock();
        }

        if (currentContextAlias == null)
            throw new InvalidOperationException("No context is currently selected.");

        return GetOrLoadSession(currentContextAlias);
    }

    public bool TryGetSession(string contextAlias, out DecompilerSession session)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _lock.EnterReadLock();
        try
        {
            return _sessionsByAlias.TryGetValue(contextAlias, out session!);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public DecompilerSession GetOrLoadSession(string contextAlias)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(contextAlias))
            throw new ArgumentException("Context alias cannot be empty.", nameof(contextAlias));

        if (TryGetSession(contextAlias, out var loadedSession))
            return loadedSession;

        lock (_activationLock)
        {
            if (TryGetSession(contextAlias, out loadedSession))
                return loadedSession;

            WorkspaceRegistryEntry? entry;
            _lock.EnterReadLock();
            try
            {
                entry = _registryState.Contexts.FirstOrDefault(candidate =>
                    string.Equals(candidate.ContextAlias, contextAlias, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _lock.ExitReadLock();
            }

            if (entry == null)
                throw new InvalidOperationException($"Context alias '{contextAlias}' is not loaded or registered.");

            LoadAssembly(CreateLoadRequest(entry));

            if (TryGetSession(entry.ContextAlias, out loadedSession))
                return loadedSession;

            throw new InvalidOperationException($"Context alias '{contextAlias}' could not be loaded.");
        }
    }

    public IReadOnlyList<string> ListRegisteredAliases()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _lock.EnterReadLock();
        try
        {
            return _registryState.Contexts
                .Select(entry => entry.ContextAlias)
                .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public bool TryGetSessionByMvid(string mvid, out DecompilerSession session)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(mvid))
        {
            session = null!;
            return false;
        }

        _lock.EnterReadLock();
        try
        {
            if (_aliasByMvid.TryGetValue(mvid, out var alias) && _sessionsByAlias.TryGetValue(alias, out session!))
                return true;

            session = null!;
            return false;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public DecompilerSession ResolveSessionForMemberId(string memberId)
    {
        if (string.IsNullOrWhiteSpace(memberId))
            throw new ArgumentException("Member ID cannot be empty.", nameof(memberId));

        var separatorIndex = memberId.IndexOf(':');
        if (separatorIndex > 0)
        {
            var prefix = memberId[..separatorIndex];
            if (LooksLikeMvid(prefix) && TryGetSessionByMvid(prefix, out var sessionByMvid))
                return sessionByMvid;
        }

        return GetCurrentSession();
    }

    public void UnloadContext(string? contextAlias = null, bool preserveRegistration = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _lock.EnterWriteLock();
        try
        {
            var aliasToUnload = string.IsNullOrWhiteSpace(contextAlias) ? CurrentContextAlias : contextAlias;
            if (string.IsNullOrWhiteSpace(aliasToUnload))
                throw new InvalidOperationException("No context is currently selected.");

            var isRegistered = _registryState.Contexts.Any(entry =>
                string.Equals(entry.ContextAlias, aliasToUnload, StringComparison.OrdinalIgnoreCase));
            var isLoaded = _sessionsByAlias.TryGetValue(aliasToUnload, out var session);
            if (!isLoaded && !isRegistered)
                throw new InvalidOperationException($"Context alias '{aliasToUnload}' is not loaded or registered.");

            var wasCurrent = string.Equals(CurrentContextAlias, aliasToUnload, StringComparison.OrdinalIgnoreCase);
            if (isLoaded)
            {
                RemoveSessionMappings(session!);
                session!.Dispose();
            }

            if (!preserveRegistration)
            {
                _registryState.Contexts.RemoveAll(entry => string.Equals(entry.ContextAlias, aliasToUnload, StringComparison.OrdinalIgnoreCase));
            }

            if (wasCurrent && preserveRegistration && isRegistered)
            {
                CurrentContextAlias = aliasToUnload;
            }
            else if (CurrentContextAlias == null)
            {
                CurrentContextAlias = _sessionsByAlias.Keys
                    .Concat(_registryState.Contexts.Select(entry => entry.ContextAlias))
                    .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            }

            SaveRegistryState();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void UnloadAllContexts(bool preserveRegistration = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _lock.EnterWriteLock();
        try
        {
            var selectedAlias = CurrentContextAlias;
            foreach (var session in _sessionsByAlias.Values)
            {
                session.Dispose();
            }

            _sessionsByAlias.Clear();
            _aliasByMvid.Clear();
            if (!preserveRegistration)
            {
                _registryState.Contexts.Clear();
                CurrentContextAlias = null;
            }
            else
            {
                CurrentContextAlias = _registryState.Contexts.Any(entry =>
                    string.Equals(entry.ContextAlias, selectedAlias, StringComparison.OrdinalIgnoreCase))
                    ? selectedAlias
                    : null;
            }
            SaveRegistryState();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _lock.EnterWriteLock();
        try
        {
            if (_disposed)
                return;

            foreach (var session in _sessionsByAlias.Values)
            {
                session.Dispose();
            }

            _sessionsByAlias.Clear();
            _aliasByMvid.Clear();
            CurrentContextAlias = null;
            _disposed = true;
        }
        finally
        {
            _lock.ExitWriteLock();
            _lock.Dispose();
        }
    }

    private static void ValidateRequest(WorkspaceLoadRequest request)
    {
        if (request.GameDir != null && request.AssemblyPath != null)
            throw new ArgumentException("Cannot specify both gameDir and assemblyPath. Use gameDir for Unity projects or assemblyPath for direct assembly loading.");

        if (request.GameDir == null && request.AssemblyPath == null)
            throw new ArgumentException("Must specify either gameDir (for Unity projects) or assemblyPath (for direct assembly loading).");
    }

    private static string NormalizeAlias(string? contextAlias)
    {
        if (string.IsNullOrWhiteSpace(contextAlias))
            return DefaultContextAlias;

        return contextAlias.Trim();
    }

    private static bool LooksLikeMvid(string prefix)
    {
        return Regex.IsMatch(prefix, "^[0-9A-Fa-f]{32}$");
    }

    private static WorkspaceLoadRequest CreateLoadRequest(WorkspaceRegistryEntry entry)
    {
        return new WorkspaceLoadRequest
        {
            GameDir = entry.GameDir,
            AssemblyPath = entry.AssemblyPath,
            AssemblyFile = entry.AssemblyFile,
            AdditionalSearchDirs = entry.AdditionalSearchDirs,
            RebuildIndex = entry.RebuildIndex,
            ContextAlias = entry.ContextAlias,
            MakeCurrent = false,
            PersistRegistration = false
        };
    }

    private void UpsertRegistryEntry(string contextAlias, WorkspaceLoadRequest request)
    {
        _registryState.Contexts.RemoveAll(entry => string.Equals(entry.ContextAlias, contextAlias, StringComparison.OrdinalIgnoreCase));
        _registryState.Contexts.Add(new WorkspaceRegistryEntry
        {
            ContextAlias = contextAlias,
            GameDir = request.GameDir,
            AssemblyPath = request.AssemblyPath,
            AssemblyFile = request.AssemblyFile,
            AdditionalSearchDirs = request.AdditionalSearchDirs,
            RebuildIndex = request.RebuildIndex
        });
        _registryState.CurrentContextAlias = CurrentContextAlias;
    }

    private void SaveRegistryState()
    {
        _registryState.CurrentContextAlias = CurrentContextAlias;

        var directory = Path.GetDirectoryName(_registryPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(_registryState, RegistrySerializerOptions);
        File.WriteAllText(_registryPath, json);
    }

    private static WorkspaceRegistryState LoadRegistryState(string registryPath)
    {
        if (!File.Exists(registryPath))
            return new WorkspaceRegistryState();

        try
        {
            var json = File.ReadAllText(registryPath);
            return JsonSerializer.Deserialize<WorkspaceRegistryState>(json, RegistrySerializerOptions)
                ?? new WorkspaceRegistryState();
        }
        catch
        {
            return new WorkspaceRegistryState();
        }
    }

    private static string GetDefaultRegistryPath()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".decompilerserver");
        }

        return Path.Combine(baseDir, "DecompilerServer", "contexts.json");
    }

    private void RemoveSessionMappings(DecompilerSession session)
    {
        _sessionsByAlias.Remove(session.ContextAlias);

        var mvid = session.ContextManager.Mvid;
        if (mvid != null &&
            _aliasByMvid.TryGetValue(mvid, out var mappedAlias) &&
            string.Equals(mappedAlias, session.ContextAlias, StringComparison.OrdinalIgnoreCase))
        {
            _aliasByMvid.Remove(mvid);

            var replacement = FindReplacementSessionForMvid(mvid);
            if (replacement != null)
                _aliasByMvid[mvid] = replacement.ContextAlias;
        }

        if (string.Equals(CurrentContextAlias, session.ContextAlias, StringComparison.OrdinalIgnoreCase))
            CurrentContextAlias = null;
    }

    private void AddSessionMappings(DecompilerSession session)
    {
        var mvid = session.ContextManager.Mvid;
        if (mvid != null && !_aliasByMvid.ContainsKey(mvid))
            _aliasByMvid[mvid] = session.ContextAlias;
    }

    private DecompilerSession? FindReplacementSessionForMvid(string mvid)
    {
        if (CurrentContextAlias != null &&
            _sessionsByAlias.TryGetValue(CurrentContextAlias, out var currentSession) &&
            string.Equals(currentSession.ContextManager.Mvid, mvid, StringComparison.OrdinalIgnoreCase))
        {
            return currentSession;
        }

        return _sessionsByAlias.Values
            .Where(candidate => string.Equals(candidate.ContextManager.Mvid, mvid, StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate.ContextAlias, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}

public sealed class DecompilerSession : IDisposable
{
    private bool _disposed;

    private DecompilerSession(
        string contextAlias,
        AssemblyContextManager contextManager,
        MemberResolver memberResolver,
        DecompilerService decompilerService,
        UsageAnalyzer usageAnalyzer,
        InheritanceAnalyzer inheritanceAnalyzer)
    {
        ContextAlias = contextAlias;
        ContextManager = contextManager;
        MemberResolver = memberResolver;
        DecompilerService = decompilerService;
        UsageAnalyzer = usageAnalyzer;
        InheritanceAnalyzer = inheritanceAnalyzer;
    }

    public string ContextAlias { get; }
    public AssemblyContextManager ContextManager { get; }
    public MemberResolver MemberResolver { get; }
    public DecompilerService DecompilerService { get; }
    public UsageAnalyzer UsageAnalyzer { get; }
    public InheritanceAnalyzer InheritanceAnalyzer { get; }

    public static DecompilerSession Create(string contextAlias, WorkspaceLoadRequest request)
    {
        var contextManager = new AssemblyContextManager();

        try
        {
            if (request.GameDir != null)
                contextManager.LoadAssembly(request.GameDir, request.AssemblyFile, request.AdditionalSearchDirs);
            else
                contextManager.LoadAssemblyDirect(request.AssemblyPath!, request.AdditionalSearchDirs);

            if (request.RebuildIndex)
                contextManager.WarmIndexes();

            var memberResolver = new MemberResolver(contextManager);
            var decompilerService = new DecompilerService(contextManager, memberResolver);
            var usageAnalyzer = new UsageAnalyzer(contextManager, memberResolver);
            var inheritanceAnalyzer = new InheritanceAnalyzer(contextManager, memberResolver);

            return new DecompilerSession(contextAlias, contextManager, memberResolver, decompilerService, usageAnalyzer, inheritanceAnalyzer);
        }
        catch
        {
            contextManager.Dispose();
            throw;
        }
    }

    public WorkspaceContextInfo ToContextInfo(bool isCurrent)
    {
        return new WorkspaceContextInfo
        {
            ContextAlias = ContextAlias,
            Mvid = ContextManager.Mvid!,
            AssemblyPath = ContextManager.AssemblyPath!,
            LoadedAtUnix = ToUnixTimeSeconds(ContextManager.LoadedAtUtc),
            TypeCount = ContextManager.TypeCount,
            MethodCount = ContextManager.GetAllTypes().Sum(type => type.Methods.Count()),
            NamespaceCount = ContextManager.NamespaceCount,
            IsCurrent = isCurrent
        };
    }

    private static long? ToUnixTimeSeconds(DateTime? utc)
    {
        if (!utc.HasValue)
            return null;

        var value = DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc);
        return new DateTimeOffset(value).ToUnixTimeSeconds();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        ContextManager.Dispose();
        _disposed = true;
    }
}

public sealed record WorkspaceLoadRequest
{
    public string? GameDir { get; init; }
    public string? AssemblyPath { get; init; }
    public string AssemblyFile { get; init; } = "Assembly-CSharp.dll";
    public string[]? AdditionalSearchDirs { get; init; }
    public bool RebuildIndex { get; init; } = true;
    public string? ContextAlias { get; init; }
    public bool MakeCurrent { get; init; } = true;
    public bool PersistRegistration { get; init; } = true;
}

public sealed record WorkspaceContextInfo
{
    public required string ContextAlias { get; init; }
    public required string Mvid { get; init; }
    public required string AssemblyPath { get; init; }
    public long? LoadedAtUnix { get; init; }
    public int TypeCount { get; init; }
    public int MethodCount { get; init; }
    public int NamespaceCount { get; init; }
    public bool IsCurrent { get; init; }
}

internal sealed record WorkspaceRegistryState
{
    public string? CurrentContextAlias { get; set; }
    public List<WorkspaceRegistryEntry> Contexts { get; init; } = new();
}

internal sealed record WorkspaceRegistryEntry
{
    public required string ContextAlias { get; init; }
    public string? GameDir { get; init; }
    public string? AssemblyPath { get; init; }
    public string AssemblyFile { get; init; } = "Assembly-CSharp.dll";
    public string[]? AdditionalSearchDirs { get; init; }
    public bool RebuildIndex { get; init; } = true;
}
