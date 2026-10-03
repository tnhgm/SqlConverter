using System.Data.SqlClient;
using ClassLibrary;

namespace ApplicationForm
{
    public sealed class SqliteToSqlServerForm : Form
    {
        private readonly TextBox txtSqlitePath = new TextBox();
        private readonly TextBox txtServer = new TextBox();
        private readonly TextBox txtDatabase = new TextBox();
        private readonly TextBox txtUser = new TextBox();
        private readonly TextBox txtPassword = new TextBox();
        private readonly CheckBox chkIntegrated = new CheckBox();
        private readonly Button btnBrowse = new Button();
        private readonly Button btnStart = new Button();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly Label status = new Label();
        private bool isRunning;

        public SqliteToSqlServerForm()
        {
            InitializeForm();
            UpdateAuthenticationControls();
        }

        private void InitializeForm()
        {
            Text = "SQLite To SQL Server Converter";
            ClientSize = new Size(720, 330);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            AddLabel("SQLite database file:", 18, 22);
            txtSqlitePath.SetBounds(180, 18, 430, 27);
            btnBrowse.Text = "Browse...";
            btnBrowse.SetBounds(620, 16, 82, 32);
            btnBrowse.Click += BrowseClick;

            AddLabel("SQL Server:", 18, 67);
            txtServer.SetBounds(180, 63, 522, 27);
            AddLabel("Database:", 18, 107);
            txtDatabase.SetBounds(180, 103, 522, 27);

            chkIntegrated.Text = "Use Windows authentication";
            chkIntegrated.SetBounds(180, 143, 260, 25);
            chkIntegrated.Checked = true;
            chkIntegrated.CheckedChanged += (_, _) => UpdateAuthenticationControls();

            AddLabel("User:", 18, 187);
            txtUser.SetBounds(180, 183, 220, 27);
            AddLabel("Password:", 420, 187);
            txtPassword.SetBounds(500, 183, 202, 27);
            txtPassword.UseSystemPasswordChar = true;

            btnStart.Text = "Start conversion";
            btnStart.SetBounds(500, 235, 202, 35);
            btnStart.Click += StartClick;

            progress.SetBounds(18, 235, 460, 22);
            progress.Minimum = 0;
            progress.Maximum = 100;
            status.SetBounds(18, 275, 684, 40);
            status.AutoEllipsis = true;

            Controls.AddRange(new Control[]
            {
                txtSqlitePath, btnBrowse, txtServer, txtDatabase, chkIntegrated,
                txtUser, txtPassword, btnStart, progress, status
            });
            FormClosing += FormClosingHandler;
        }

        private void AddLabel(string text, int x, int y)
        {
            Label label = new Label
            {
                Text = text,
                AutoSize = true,
                Location = new Point(x, y + 4)
            };
            Controls.Add(label);
        }

        private void BrowseClick(object sender, EventArgs e)
        {
            using OpenFileDialog dialog = new OpenFileDialog
            {
                Filter = "SQLite database files|*.db;*.sqlite;*.sqlite3|All files|*.*",
                CheckFileExists = true,
                RestoreDirectory = true
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                txtSqlitePath.Text = dialog.FileName;
        }

        private void UpdateAuthenticationControls()
        {
            txtUser.Enabled = !chkIntegrated.Checked;
            txtPassword.Enabled = !chkIntegrated.Checked;
        }

        private async void StartClick(object sender, EventArgs e)
        {
            if (isRunning)
                return;

            if (string.IsNullOrWhiteSpace(txtSqlitePath.Text) ||
                string.IsNullOrWhiteSpace(txtServer.Text) ||
                string.IsNullOrWhiteSpace(txtDatabase.Text))
            {
                MessageBox.Show(this, "SQLite path, SQL Server and database are required.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!chkIntegrated.Checked && string.IsNullOrWhiteSpace(txtUser.Text))
            {
                MessageBox.Show(this, "Enter the SQL Server user name.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder
            {
                DataSource = txtServer.Text.Trim(),
                InitialCatalog = txtDatabase.Text.Trim(),
                IntegratedSecurity = chkIntegrated.Checked,
                TrustServerCertificate = true,
                ConnectTimeout = 30
            };
            if (!chkIntegrated.Checked)
            {
                builder.UserID = txtUser.Text.Trim();
                builder.Password = txtPassword.Text;
            }

            isRunning = true;
            btnStart.Enabled = false;
            btnBrowse.Enabled = false;
            progress.Value = 0;
            status.Text = "Starting conversion...";
            try
            {
                await Task.Run(() => SQLiteToSqlServer.Convert(
                    txtSqlitePath.Text.Trim(),
                    builder.ConnectionString,
                    (percent, message) => UpdateProgress(percent, message)));

                progress.Value = 100;
                status.Text = "Conversion finished.";
                MessageBox.Show(this, "SQLite database was transferred to SQL Server.", "Conversion Finished", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                status.Text = "Conversion failed.";
                MessageBox.Show(this, ex.Message, "Conversion Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                isRunning = false;
                btnStart.Enabled = true;
                btnBrowse.Enabled = true;
            }
        }

        private void UpdateProgress(int percent, string message)
        {
            if (IsDisposed)
                return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<int, string>(UpdateProgress), percent, message);
                return;
            }
            progress.Value = Math.Max(progress.Minimum, Math.Min(progress.Maximum, percent));
            status.Text = message;
        }

        private void FormClosingHandler(object sender, FormClosingEventArgs e)
        {
            if (isRunning)
            {
                e.Cancel = true;
                MessageBox.Show(this, "Please wait until the conversion finishes.", "Conversion in progress", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }
}
