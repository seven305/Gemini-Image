namespace GeminiBatch.WinForms;

/// <summary>
/// A <see cref="DataGridView"/> with double buffering on, so per-row status updates repaint without flicker.
/// <c>DoubleBuffered</c> is protected, hence the subclass; use it in the designer like a normal grid.
/// </summary>
public sealed class BufferedDataGridView : DataGridView
{
    public BufferedDataGridView()
    {
        DoubleBuffered = true;
    }
}
