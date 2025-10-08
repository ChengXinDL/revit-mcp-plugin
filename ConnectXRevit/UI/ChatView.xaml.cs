using System.Windows;

namespace ConnectXRevit.UI
{
    /// <summary>
    /// ChatView.xaml 的交互逻辑
    /// </summary>
    public partial class ChatView : Window
    {
        public ChatView()
        {
            InitializeComponent();
            // 可以在这里添加视图相关的初始化逻辑
            Closing += OnClosing;
        }

        /// <summary>
        /// 窗口关闭时的处理
        /// </summary>
        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // 断开WebSocket连接（如果需要）
            if (DataContext is ViewModels.ChatViewModel viewModel)
            {
                if (viewModel.IsConnected)
                {
                    viewModel.DisconnectCommand.Execute(null);
                }
            }
        }

        // 可以添加其他UI交互事件（如快捷键处理等）
    }
}
