using System.Runtime.ExceptionServices;
using FlaUI.Core.Input;

namespace LeafCalendar.UITests.Support;

/// <summary>
/// Drags data from the test process onto a point on screen, as dragging it there from another app does (OLE drag and
/// drop, on its own STA thread). The mouse button stays up, so the drag enters whatever is under the pointer and drops
/// at once.
/// </summary>
static class DragSource
{
    /// <summary>Drops <paramref name="data"/> at <paramref name="point"/> (screen pixels) and returns what the target took (None when it refused).</summary>
    public static System.Windows.Forms.DragDropEffects DropAt(System.Drawing.Point point, System.Windows.Forms.DataObject data)
    {
        Mouse.MoveTo(point);
        Thread.Sleep(100);

        ExceptionDispatchInfo? failure = null;
        var effect = System.Windows.Forms.DragDropEffects.None;
        var thread = new Thread(() =>
        {
            try
            {
                using var source = new System.Windows.Forms.Control();
                effect = source.DoDragDrop(data, System.Windows.Forms.DragDropEffects.Copy | System.Windows.Forms.DragDropEffects.Move | System.Windows.Forms.DragDropEffects.Link);
            }
            catch (InvalidOperationException ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The drag never finished.");
        failure?.Throw();
        return effect;
    }
}
