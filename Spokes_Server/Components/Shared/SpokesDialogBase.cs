using Microsoft.AspNetCore.Components;
using MudBlazor;
using Spokes_Server.Core.Extensions;

namespace Spokes_Server.Components.Shared;

public abstract class SpokesDialogBase<T> : SpokesComponentBase where T : class
{
    [CascadingParameter]
    public IMudDialogInstance MudDialog { get; set; } = default!;

    public T Model { get; set; } = default!;

    [Inject] protected global::Spokes_Server.Aggregate.Database Db { get; set; } = default!;

    private System.Timers.Timer? _autoSaveTimer;
    private System.Reflection.MethodInfo? _saveMethod;
    private object? _repositoryInstance;

    protected override void OnInitialized()
    {
        base.OnInitialized();
        
        var type = GetType();
        var props = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        var paramProp = props.FirstOrDefault(p => p.PropertyType == typeof(T) && p.GetCustomAttributes(typeof(ParameterAttribute), true).Any());
        
        if (paramProp != null)
        {
            var original = paramProp.GetValue(this) as T;
            if (original != null)
            {
                Model = original.DeepClone();
            }
        }

        SetupAutoSave();
    }

    private void SetupAutoSave()
    {
        var dbProps = typeof(global::Spokes_Server.Aggregate.Database).GetProperties();
        foreach (var prop in dbProps)
        {
            var propType = prop.PropertyType;
            while (propType != null && propType != typeof(object))
            {
                if (propType.IsGenericType && propType.GetGenericTypeDefinition() == typeof(Spokes_Server.Core.Data.JsonRepository<>))
                {
                    if (propType.GetGenericArguments()[0] == typeof(T))
                    {
                        _repositoryInstance = prop.GetValue(Db);
                        _saveMethod = propType.GetMethod("Save", new[] { typeof(T) });
                        break;
                    }
                }
                propType = propType.BaseType;
            }
            if (_saveMethod != null) break;
        }

        if (_saveMethod != null && _repositoryInstance != null)
        {
            _autoSaveTimer = new System.Timers.Timer(5000);
            _autoSaveTimer.AutoReset = true;
            _autoSaveTimer.Elapsed += (s, e) => 
            {
                if (Model != null)
                {
                    _ = SafeInvokeAsync(() => 
                    {
                        _saveMethod.Invoke(_repositoryInstance, new object[] { Model });
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
            _saveMethod.Invoke(_repositoryInstance, new object[] { Model });
        }
        MudDialog.Close(DialogResult.Ok(Model));
    }

    protected virtual void Close()
    {
        if (_autoSaveTimer != null) _autoSaveTimer.Stop();
        MudDialog.Cancel();
    }
}
