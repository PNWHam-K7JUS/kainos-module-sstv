/*  AdjustForm.cs

Adjust: the last picture received, straightened (slant) and moved left or right, drawn again from its audio as
the buttons are pressed. For the odd picture the automatic fit gets wrong (a weak one, or one joined part way).

This file is part of the Kainos SSTV module. Copyright (C) 2026 Justin Cron K7JUS. GPLv3 or later.
*/

using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Kainos.Modules;

namespace Kainos.Sstv.UI
{
    internal class AdjustForm : Form
    {
        private readonly Label _slant, _shift, _note;
        public double Ppm { get; private set; }
        public double Ms { get; private set; }
        public bool Saved { get; private set; } = true;     // nothing changed since the last save
        public event Action Changed;
        public event Action SaveWanted;

        public AdjustForm(IKainosHost host, string what)
        {
            IKainosTheme t = host.Theme;
            int s(float v) => t.S(v);
            Text = "SSTV: adjust " + what;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ClientSize = new Size(s(430), s(178));
            Font = t.Font(s(12), false);
            ToolTip tip = new ToolTip();

            int y = s(12);
            Func<string, int, int, string, Button> button = (text, x, w, tt) =>
            {
                Button b = new Button { Text = text, Location = new Point(s(x), y), Size = new Size(s(w), s(28)) };
                if (tt != null) tip.SetToolTip(b, tt);
                Controls.Add(b);
                return b;
            };

            // slant: the line length, in parts per million of the mode's
            Controls.Add(new Label { Text = "Slant", Location = new Point(s(12), y + s(5)), Size = new Size(s(60), s(20)) });
            const string slantTip = "Straightens a picture that leans: try one way, and if it gets worse, the other";
            button("<<", 76, 44, slantTip).Click += (o, e) => slant(-100);
            button("<", 124, 44, slantTip).Click += (o, e) => slant(-10);
            _slant = new Label { TextAlign = ContentAlignment.MiddleCenter, Location = new Point(s(172), y), Size = new Size(s(90), s(28)) };
            Controls.Add(_slant);
            button(">", 266, 44, slantTip).Click += (o, e) => slant(10);
            button(">>", 314, 44, slantTip).Click += (o, e) => slant(100);

            // shift: where the lines start (the picture moved left or right, the part off one edge brought round)
            y += s(38);
            Controls.Add(new Label { Text = "Move", Location = new Point(s(12), y + s(5)), Size = new Size(s(60), s(20)) });
            const string moveTip = "Moves the picture left or right, for one that wraps round (part of its left edge on the right)";
            button("<<", 76, 44, moveTip).Click += (o, e) => move(2);
            button("<", 124, 44, moveTip).Click += (o, e) => move(0.25);
            _shift = new Label { TextAlign = ContentAlignment.MiddleCenter, Location = new Point(s(172), y), Size = new Size(s(90), s(28)) };
            Controls.Add(_shift);
            button(">", 266, 44, moveTip).Click += (o, e) => move(-0.25);
            button(">>", 314, 44, moveTip).Click += (o, e) => move(-2);

            y += s(38);
            _note = new Label { Location = new Point(s(12), y), Size = new Size(s(406), s(20)), ForeColor = t.Dim };
            Controls.Add(_note);

            y += s(28);
            button("Reset", 12, 80, "Back to the picture as it was received").Click += (o, e) => { Ppm = 0; Ms = 0; changed(); };
            Button save = button("Save", 246, 84, "Save the picture as it is now (in place of the one saved)");
            save.Click += (o, e) => { Saved = true; SaveWanted?.Invoke(); };
            Button close = button("Close", 334, 84, null);
            close.Click += (o, e) => Close();
            CancelButton = close;
            show();
            t.Style(this);
        }

        public string Note { set { _note.Text = value ?? ""; } }

        private void slant(double d) { Ppm += d; changed(); }
        private void move(double d) { Ms += d; changed(); }

        private void changed()
        {
            Saved = false;
            show();
            Changed?.Invoke();
        }

        private void show()
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            _slant.Text = Ppm.ToString("+0;-0;0", ic) + " ppm";
            _shift.Text = Ms.ToString("+0.00;-0.00;0", ic) + " ms";
        }
    }
}
