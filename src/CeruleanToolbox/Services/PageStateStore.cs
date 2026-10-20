using System.Collections;
using System.Reflection;
using Microsoft.AspNetCore.Components;

namespace CeruleanToolbox.Services;

/// <summary>
/// Blazor throws a page away when the user navigates to another one, so coming back showed an empty page. A page calls
/// <see cref="Attach"/> once when it starts: the values of its data fields (selection, filters, loaded lists, open tab) are put
/// back, and when the user leaves for a different page they are kept again. The state belongs to the active connection: switching
/// connection (or the explicit "?open=" deep link) starts the page from scratch.
/// </summary>
public sealed class PageStateStore
{
    private static readonly string[] NeverRestored = { "IsBusy", "IsLoading" };

    private readonly NavigationManager _navigation;
    private readonly ConnectionManagerService _connections;
    private readonly Dictionary<Type, Dictionary<FieldInfo, object?>> _saved = new();
    private string? _savedForConnection;

    public PageStateStore(NavigationManager navigation, ConnectionManagerService connections)
    {
        _navigation = navigation;
        _connections = connections;
    }

    public void Attach(ComponentBase page)
    {
        var type = page.GetType();
        var connectionId = _connections.ActiveConnection?.Id;

        if (_savedForConnection != connectionId)
        {
            _saved.Clear();
            _savedForConnection = connectionId;
        }

        var deepLink = new Uri(_navigation.Uri).Query.Contains("open=", StringComparison.OrdinalIgnoreCase);
        if (!deepLink && _saved.TryGetValue(type, out var values))
        {
            foreach (var (field, value) in values)
            {
                try { field.SetValue(page, value); } catch { /* a field that cannot be restored just starts empty */ }
            }
        }

        var startPath = new Uri(_navigation.Uri).AbsolutePath;
        void OnLocationChanged(object? sender, Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs e)
        {
            // Same page with another query string (a deep link): the page stays alive, nothing to keep yet.
            if (string.Equals(new Uri(e.Location).AbsolutePath, startPath, StringComparison.OrdinalIgnoreCase)) return;

            _navigation.LocationChanged -= OnLocationChanged;
            if (_savedForConnection == _connections.ActiveConnection?.Id) _saved[type] = Capture(page);
        }

        _navigation.LocationChanged += OnLocationChanged;
    }

    private static Dictionary<FieldInfo, object?> Capture(ComponentBase page)
    {
        var result = new Dictionary<FieldInfo, object?>();
        for (var t = page.GetType(); t is not null && t != typeof(ComponentBase); t = t.BaseType)
        {
            var injected = t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(p => p.GetCustomAttribute<InjectAttribute>() is not null)
                .Select(p => $"<{p.Name}>k__BackingField")
                .ToHashSet();

            foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.IsInitOnly || injected.Contains(field.Name) || !IsPlainData(field)) continue;
                if (NeverRestored.Any(n => field.Name == $"<{n}>k__BackingField")) continue;
                result[field] = field.GetValue(page);
            }
        }

        return result;
    }

    // Data only: no services, tasks, timers, cancellation sources, delegates or framework objects.
    private static bool IsPlainData(FieldInfo field)
    {
        var type = field.FieldType;
        if (type.IsPointer || type.IsByRef) return false;
        if (typeof(Delegate).IsAssignableFrom(type) || typeof(Task).IsAssignableFrom(type)) return false;
        if (typeof(IDisposable).IsAssignableFrom(type) || typeof(IAsyncDisposable).IsAssignableFrom(type)) return false;
        if (type.Namespace?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true) return false;
        if (type.Namespace?.StartsWith("Microsoft.JSInterop", StringComparison.Ordinal) == true) return false;
        if (type.Namespace?.StartsWith("System.Threading", StringComparison.Ordinal) == true) return false;
        if (type.Namespace == "CeruleanToolbox.Services" && !type.IsEnum && !typeof(IEnumerable).IsAssignableFrom(type) && type.Name.EndsWith("Service")) return false;
        return true;
    }
}
