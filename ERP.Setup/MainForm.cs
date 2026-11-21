using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ERP.Setup
{
    /// <summary>نافذة واحدة: عنوان السيرفر (جاهز) + كلمة المرور + زر «تثبيت». لا خيارات أخرى.</summary>
    public class MainForm : Form
    {
        private readonly Options _o;
        private readonly TextBox _host = new TextBox();
        private readonly TextBox _pwd = new TextBox();
        private readonly Button _go = new Button();
        private readonly ProgressBar _bar = new ProgressBar();
        private readonly Label _status = new Label();

        public MainForm(Options o)
        {
            _o = o;
            RightToLeft = RightToLeft.Yes;
            RightToLeftLayout = true;
            Text = "تثبيت نظام معمل المياه";
            Font = new Font("Segoe UI", 10.5f);
            ClientSize = new Size(500, 440);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(245, 248, 252);
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("setup.ico"))
                    if (s != null) Icon = new Icon(s);
            }
            catch (Exception) { }

            var header = new Panel { Dock = DockStyle.Top, Height = 84, BackColor = Color.FromArgb(20, 81, 138) };
            var logo = new PictureBox { Size = new Size(56, 56), Location = new Point(420, 14), SizeMode = PictureBoxSizeMode.Zoom };
            try { if (Icon != null) logo.Image = new Icon(Icon, 64, 64).ToBitmap(); } catch (Exception) { }
            var title = new Label
            {
                Text = "تثبيت نظام معمل المياه", ForeColor = Color.White, AutoSize = false,
                Font = new Font("Segoe UI", 15f, FontStyle.Bold), Size = new Size(380, 56), Location = new Point(30, 14),
                TextAlign = ContentAlignment.MiddleRight
            };
            header.Controls.Add(logo);
            header.Controls.Add(title);
            Controls.Add(header);

            var l1 = new Label { Text = "عنوان السيرفر", AutoSize = false, Size = new Size(440, 24), Location = new Point(30, 104), TextAlign = ContentAlignment.MiddleRight };
            _host.Text = o.Host; _host.Width = 440; _host.Location = new Point(30, 130);
            _host.TextAlign = HorizontalAlignment.Left; _host.RightToLeft = RightToLeft.No;
            var l2 = new Label { Text = "كلمة مرور الاتصال بالسيرفر (تُكتب مرة واحدة)", AutoSize = false, Size = new Size(440, 24), Location = new Point(30, 176), TextAlign = ContentAlignment.MiddleRight };
            _pwd.Width = 440; _pwd.Location = new Point(30, 202); _pwd.UseSystemPasswordChar = true;
            _pwd.TextAlign = HorizontalAlignment.Left; _pwd.RightToLeft = RightToLeft.No;

            _go.Text = "تثبيت"; _go.Size = new Size(160, 42); _go.Location = new Point(170, 254);
            _go.BackColor = Color.FromArgb(20, 81, 138); _go.ForeColor = Color.White; _go.FlatStyle = FlatStyle.Flat;
            _go.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            _go.Click += async (s, e) => await RunAsync();
            AcceptButton = _go;

            _bar.Location = new Point(30, 318); _bar.Width = 440; _bar.Height = 18; _bar.Visible = false;
            _status.AutoSize = false; _status.Location = new Point(30, 342); _status.Size = new Size(440, 84);
            _status.TextAlign = ContentAlignment.TopCenter;

            Controls.AddRange(new Control[] { l1, _host, l2, _pwd, _go, _bar, _status });
        }

        private async Task RunAsync()
        {
            if (string.IsNullOrWhiteSpace(_host.Text)) { Fail("اكتب عنوان السيرفر."); return; }
            if (_pwd.Text.Length == 0) { Fail("اكتب كلمة المرور."); _pwd.Focus(); return; }
            _o.Host = _host.Text.Trim();
            _o.Password = _pwd.Text;
            _go.Enabled = _host.Enabled = _pwd.Enabled = false;
            _bar.Visible = true; _bar.Value = 0;
            _status.ForeColor = Color.FromArgb(60, 60, 60);
            try
            {
                var exe = await Task.Run(() => SetupCore.Install(_o, (p, t) => BeginInvoke((Action)(() =>
                {
                    _bar.Value = Math.Max(0, Math.Min(100, p));
                    _status.Text = t;
                }))));
                _status.ForeColor = Color.FromArgb(31, 138, 76);
                _status.Text = "تم التثبيت بنجاح. سيفتح البرنامج الآن.";
                SetupCore.Launch(exe);
                await Task.Delay(1500);
                Close();
            }
            catch (Exception ex)
            {
                Fail(ex.Message);
                _go.Enabled = _host.Enabled = _pwd.Enabled = true;
                _bar.Visible = false;
                _pwd.SelectAll(); _pwd.Focus();
            }
        }

        private void Fail(string message)
        {
            _status.ForeColor = Color.FromArgb(192, 38, 45);
            _status.Text = message;
        }
    }
}
