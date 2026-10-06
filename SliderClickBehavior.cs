using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AudioRouteControl;

internal static class SliderClickBehavior
{
    private static readonly HashSet<Slider> Dragging = new();

    public static void Attach(Slider slider)
    {
        slider.PreviewMouseLeftButtonDown += OnMouseDown;
        slider.PreviewMouseMove += OnMouseMove;
        slider.PreviewMouseLeftButtonUp += OnMouseUp;
    }

    private static void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider || slider.ActualWidth <= 0) return;
        SetValue(slider, e.GetPosition(slider));
        Dragging.Add(slider);
        slider.CaptureMouse();
        e.Handled = true;
    }

    private static void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is Slider slider && Dragging.Contains(slider) && e.LeftButton == MouseButtonState.Pressed)
            SetValue(slider, e.GetPosition(slider));
    }

    private static void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider slider && Dragging.Remove(slider))
        {
            slider.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private static void SetValue(Slider slider, Point point)
    {
        var ratio = Math.Clamp(point.X / slider.ActualWidth, 0, 1);
        slider.Value = slider.Minimum + (slider.Maximum - slider.Minimum) * ratio;
    }
}
