using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ConnectXRevit.Core;
using ConnectXRevit.Models.Chat;

namespace ConnectXRevit.ViewModels
{
    public class ChatViewModel : INotifyPropertyChanged
    {
        private readonly SocketService _socketService;
        private string _userInput;
        private bool _isConnected;
        private string _connectionStatus;

        public ObservableCollection<ChatMessage> Messages { get; } = new ObservableCollection<ChatMessage>();

        public string UserInput
        {
            get => _userInput;
            set
            {
                _userInput = value;
                OnPropertyChanged();
                SendCommand.RaiseCanExecuteChanged();
            }
        }

        public bool IsConnected
        {
            get => _isConnected;
            set
            {
                _isConnected = value;
                ConnectionStatus = value ? "已连接" : "未连接";
                OnPropertyChanged();
                ConnectCommand.RaiseCanExecuteChanged();
                DisconnectCommand.RaiseCanExecuteChanged();
            }
        }

        public string ConnectionStatus
        {
            get => _connectionStatus;
            set
            {
                _connectionStatus = value;
                OnPropertyChanged();
            }
        }

        public RelayCommand ConnectCommand { get; }
        public RelayCommand DisconnectCommand { get; }
        public RelayCommand SendCommand { get; }

        public ChatViewModel()
        {
            _socketService = SocketService.Instance;

            // 订阅SocketService事件
            _socketService.ChatMessageReceived += OnChatMessageReceived;

            // 初始化命令
            ConnectCommand = new RelayCommand(Connect, () => !IsConnected);
            DisconnectCommand = new RelayCommand(Disconnect, () => IsConnected);
            SendCommand = new RelayCommand(SendMessage, () => IsConnected && !string.IsNullOrWhiteSpace(UserInput));

            IsConnected = _socketService.IsRunning;
        }

        private void Connect()
        {
            try
            {
                if (!_socketService.IsRunning)
                {
                    _socketService.Start();
                }
                IsConnected = true;
            }
            catch (Exception ex)
            {
                AddSystemMessage($"连接失败: {ex.Message}");
            }
        }

        private void Disconnect()
        {
            try
            {
                if (_socketService.IsRunning)
                {
                    _socketService.Stop();
                }
                IsConnected = false;
            }
            catch (Exception ex)
            {
                AddSystemMessage($"断开连接失败: {ex.Message}");
            }
        }

        private async void SendMessage()
        {
            if (string.IsNullOrWhiteSpace(UserInput))
                return;

            try
            {
                var message = new HumanMessage
                {
                    Id = Guid.NewGuid().ToString(),
                    Content = UserInput,
                    Timestamp = DateTime.Now
                };

                // 添加到本地消息列表
                Messages.Add(message);

                // 发送到Agent
                await _socketService.SendChatMessage(message);

                // 清空输入框
                UserInput = string.Empty;
            }
            catch (Exception ex)
            {
                AddSystemMessage($"发送消息失败: {ex.Message}");
            }
        }

        private void OnChatMessageReceived(ChatMessage message)
        {
            // 在UI线程上更新消息列表
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                Messages.Add(message);
            });
        }

        private void AddSystemMessage(string content)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                Messages.Add(new AIMessage
                {
                    Id = Guid.NewGuid().ToString(),
                    Content = content,
                    Timestamp = DateTime.Now,
                    AgentName = "系统"
                });
            });
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
