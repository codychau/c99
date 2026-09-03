using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace C99
{
    /// <summary>
    /// 支持在按钮上直接显示忙碌光标的 Button（WinUI3 的 ProtectedCursor 仅子类可访问）。
    /// </summary>
    public sealed class BusyCursorButton : Button
    {
        public void SetBusy(bool busy)
        {
            if (busy)
            {
                ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Wait);
            }
            else
            {
                ProtectedCursor?.Dispose();
                ProtectedCursor = null;
            }
        }
    }
}