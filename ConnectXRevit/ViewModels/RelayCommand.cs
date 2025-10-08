using System;
using System.Windows.Input;

namespace ConnectXRevit.ViewModels
{
    /// <summary>
    /// ICommand接口的具体实现，支持命令执行和状态变更通知
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="execute">命令执行的方法</param>
        /// <param name="canExecute">判断命令是否可执行的方法</param>
        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        /// <summary>
        /// 检查命令是否可执行
        /// </summary>
        public bool CanExecute(object parameter)
        {
            return _canExecute == null || _canExecute();
        }

        /// <summary>
        /// 执行命令
        /// </summary>
        public void Execute(object parameter)
        {
            _execute();
        }

        /// <summary>
        /// 当命令可执行状态变化时触发
        /// </summary>
        public event EventHandler CanExecuteChanged;

        /// <summary>
        /// 手动触发命令可执行状态变化（关键方法）
        /// </summary>
        public void RaiseCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
