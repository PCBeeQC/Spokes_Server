using Spokes_Server.Components.Layout;
using System;

namespace Spokes_Server.Core.Services.Communication
{
    public class ShareTargetStateService
    {
        private readonly object _lock = new();
        private MainLayout.SharePayload? _pendingPayload;
        private string? _targetChannelId;

        public MainLayout.SharePayload? PendingPayload
        {
            get { lock (_lock) return _pendingPayload; }
        }
        public string? TargetChannelId
        {
            get { lock (_lock) return _targetChannelId; }
        }

        public event Action? OnPayloadReceived;

        public void SetPayload(MainLayout.SharePayload payload, string channelId)
        {
            lock (_lock)
            {
                _pendingPayload = payload;
                _targetChannelId = channelId;
            }
            OnPayloadReceived?.Invoke();
        }

        public void Clear()
        {
            lock (_lock)
            {
                _pendingPayload = null;
                _targetChannelId = null;
            }
        }
    }
}
