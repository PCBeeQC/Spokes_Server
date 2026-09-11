using System;

namespace Spokes_Server.Core.Models.Core
{
    public class UploadProgressState
    {
        private double _progress = 0;
        private string _statusText = "Uploading...";
        private bool _isUploading = false;

        public event Action? OnStateChanged;

        public double Progress
        {
            get => _progress;
            set
            {
                if (_progress != value)
                {
                    _progress = value;
                    OnStateChanged?.Invoke();
                }
            }
        }

        public string StatusText
        {
            get => _statusText;
            set
            {
                if (_statusText != value)
                {
                    _statusText = value;
                    OnStateChanged?.Invoke();
                }
            }
        }

        public bool IsUploading
        {
            get => _isUploading;
            set
            {
                if (_isUploading != value)
                {
                    _isUploading = value;
                    OnStateChanged?.Invoke();
                }
            }
        }
    }
}
