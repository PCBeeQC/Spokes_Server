using System.Reflection;
using Timer = System.Timers.Timer;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Extensions;

namespace Spokes_Server.Components.Shared;

public abstract class SpokesDialogBase<T> : SpokesComponentBase where T : class
{
    [CascadingParameter]
    public IMudDialogInstance MudDialog { get; set; } = default!;

    public T Model { get; set; } = default!;

    [Inject] protected Database Db { get; set; } = default!;

    private Timer? _autoSaveTimer;
    private MethodInfo? _saveMethod;
    private object? _repositoryInstance;

    protected override void OnInitialized()
    {
        base.OnInitialized();
        
        var type = GetType();
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var paramProp = props.FirstOrDefault(p => p.PropertyType == typeof(T) && p.IsDefined(typeof(ParameterAttribute), true));
        
        if (paramProp?.GetValue(this) is T original)
        {
            Model = original.DeepClone();
        }

        SetupAutoSave();
    }

    private void SetupAutoSave()
    {
        var dbProps = typeof(Database).GetProperties();
        foreach (var prop in dbProps)
        {
            var propType = prop.PropertyType;
            while (propType != null && propType != typeof(object))
            {
                if (propType.IsGenericType && propType.GetGenericTypeDefinition() == typeof(JsonRepository<>))
                {
                    if (propType.GetGenericArguments()[0] == typeof(T))
                    {
                        _repositoryInstance = prop.GetValue(Db);
                        _saveMethod = propType.GetMethod("Save", [typeof(T)]);
                        break;
                    }
                }
                propType = propType.BaseType;
            }
            if (_saveMethod != null) break;
        }

        if (_saveMethod != null && _repositoryInstance != null)
        {
            _autoSaveTimer = new Timer(5000)
            {
                AutoReset = true
            };
            _autoSaveTimer.Elapsed += (s, e) => 
            {
                if (Model != null)
                {
                    _ = SafeInvokeAsync(() => 
                    {
                        _saveMethod.Invoke(_repositoryInstance, [Model]);
                    });
                }
            };
            _autoSaveTimer.Start();
            _disposables.Add(_autoSaveTimer);
        }
    }

    protected void TriggerAutoSave()
    {
        if (_autoSaveTimer != null)
        {
            _autoSaveTimer.Stop();
            _autoSaveTimer.Start();
        }
    }

    protected virtual void Submit()
    {
        if (_autoSaveTimer != null) _autoSaveTimer.Stop();
        if (_saveMethod != null && _repositoryInstance != null && Model != null)
        {
            _saveMethod.Invoke(_repositoryInstance, [Model]);
        }
        MudDialog.Close(DialogResult.Ok(Model));
    }

    protected virtual void Close()
    {
        if (_autoSaveTimer != null) _autoSaveTimer.Stop();
        MudDialog.Cancel();
    }
}
