using System;
using System.Windows;
using System.Windows.Controls;
using OpenVSA.Measurement.State;

namespace OpenVSA.Ui
{
    /// <summary>
    /// The save-state dialog, which names what a state does not contain (<c>REQ-STA-002</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A dialog of its own rather than a bare file picker, for one reason: the requirement is
    /// explicit that the exclusions must be stated here rather than left for the user to discover.
    /// A file picker has nowhere to say it, and finding out that your data registers were not in the
    /// setup you sent a colleague is the sort of discovery that happens at the worst moment.
    /// </para>
    /// <para>
    /// The notice comes from <see cref="StateFile.ExclusionNotice"/> rather than being written into
    /// the markup, so the wording and the behaviour it describes cannot drift apart.
    /// </para>
    /// </remarks>
    public sealed class StateSaveDialog : Window
    {
        private readonly TextBox _path;

        /// <summary>Creates the dialog.</summary>
        /// <param name="suggestedPath">The path to offer.</param>
        public StateSaveDialog(string suggestedPath)
        {
            Title = "Save state";
            SizeToContent = SizeToContent.Height;
            Width = 560.0;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            NoticeText = StateFile.ExclusionNotice;

            var notice = new TextBlock
            {
                Text = NoticeText,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12.0, 12.0, 12.0, 8.0),
            };

            _path = new TextBox
            {
                Text = suggestedPath ?? string.Empty,
                Margin = new Thickness(12.0, 0.0, 12.0, 8.0),
            };

            var browse = new Button { Content = "Browse…", MinWidth = 88.0, Margin = new Thickness(4.0) };
            var save = new Button { Content = "Save", IsDefault = true, MinWidth = 88.0, Margin = new Thickness(4.0) };
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88.0, Margin = new Thickness(4.0) };

            browse.Click += (sender, e) => Browse();
            save.Click += (sender, e) => Finish(true);
            cancel.Click += (sender, e) => Finish(false);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8.0, 0.0, 8.0, 8.0),
            };

            buttons.Children.Add(browse);
            buttons.Children.Add(save);
            buttons.Children.Add(cancel);

            var panel = new StackPanel();
            panel.Children.Add(notice);
            panel.Children.Add(_path);
            panel.Children.Add(buttons);

            Content = panel;
        }

        /// <summary>The exclusion notice this dialog shows.</summary>
        public string NoticeText { get; }

        /// <summary>Where the state will be written.</summary>
        public string Path
        {
            get { return _path.Text; }
            set { _path.Text = value ?? string.Empty; }
        }

        private void Browse()
        {
            var picker = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "OpenVSA state (*" + StateFile.Extension + ")|*" + StateFile.Extension,
                FileName = _path.Text,
                Title = "Save state",
            };

            if (picker.ShowDialog(this) == true)
            {
                _path.Text = picker.FileName;
            }
        }

        private void Finish(bool accepted)
        {
            if (accepted && string.IsNullOrWhiteSpace(_path.Text))
            {
                return;
            }

            try
            {
                DialogResult = accepted;
            }
            catch (InvalidOperationException)
            {
                // Shown other than modally; it still closes.
            }

            Close();
        }
    }
}
