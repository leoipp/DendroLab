using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Security.Cryptography;
using System.IO;
using System.ComponentModel;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal static class DlbPasswordDialog
{
    internal static DlbEncryptionKey? Create(Window owner)
    {
        var password = new PasswordBox { MaxLength = 1024, Margin = new Thickness(0, 6, 0, 8) };
        var confirmation = new PasswordBox { MaxLength = 1024, Margin = new Thickness(0, 6, 0, 8) };
        var error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) };
        var content = new StackPanel { Margin = new Thickness(20) };
        content.Children.Add(new TextBlock { Text = "Defina a senha que protegerá o arquivo DLB. Será solicitada ao abrir o projeto.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        content.Children.Add(new TextBlock { Text = "Senha (mínimo de 12 caracteres)" }); content.Children.Add(password);
        content.Children.Add(new TextBlock { Text = "Confirme a senha" }); content.Children.Add(confirmation);
        content.Children.Add(new TextBlock { Text = "A senha não é armazenada. Se for perdida, o DendroLab não poderá recuperar os dados.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) });
        content.Children.Add(error);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var dialog = Ui.Dialog("Proteger projeto DLB", content, 500, 320);
        dialog.Owner = owner; dialog.SizeToContent = SizeToContent.Height;
        DlbEncryptionKey? result = null;
        var cancel = Ui.Button("Cancelar", () => dialog.DialogResult = false);
        var accept = Ui.Button("Criar projeto protegido", () =>
        {
            try
            {
                string candidate = password.Password;
                if (candidate.Length < 12) throw new ArgumentException("A senha precisa ter pelo menos 12 caracteres.");
                if (!string.Equals(candidate, confirmation.Password, StringComparison.Ordinal))
                    throw new ArgumentException("As senhas não coincidem.");
                result = DlbEncryptionKey.Create(candidate.AsSpan());
                password.Clear(); confirmation.Clear();
                dialog.DialogResult = true;
            }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException)
            {
                error.Text = ex.Message;
                password.Clear(); confirmation.Clear(); password.Focus();
            }
        });
        accept.IsDefault = true; cancel.IsCancel = true;
        actions.Children.Add(cancel); actions.Children.Add(accept); content.Children.Add(actions);
        dialog.Loaded += (_, _) => password.Focus();
        return dialog.ShowDialog() == true ? result : null;
    }

    internal static DlbEncryptionKey? Unlock(Window owner, string path)
    {
        string? savedPassword = WindowsCredentialStore.ReadDlbPassword(path);
        if (savedPassword != null)
        {
            try { return DlbEncryptionKey.UnlockFile(path, savedPassword.AsSpan()); }
            catch (CryptographicException) { WindowsCredentialStore.RemoveDlbPassword(path); }
        }

        var password = new PasswordBox { MaxLength = 1024, Margin = new Thickness(0, 8, 0, 8) };
        var remember = new CheckBox
        {
            Content = "Lembrar a senha neste computador por 7 dias",
            Margin = new Thickness(0, 0, 0, 8),
            IsChecked = savedPassword != null,
        };
        var error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) };
        var content = new StackPanel { Margin = new Thickness(20) };
        content.Children.Add(new TextBlock { Text = $"Informe a senha para abrir {Path.GetFileName(path)}.", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(password); content.Children.Add(remember); content.Children.Add(error);
        var dialog = Ui.Dialog("Desbloquear projeto DLB", content, 500, 220);
        dialog.Owner = owner; dialog.SizeToContent = SizeToContent.Height;
        DlbEncryptionKey? result = null;
        var cancel = Ui.Button("Cancelar", () => dialog.DialogResult = false);
        string? reminderError = null;
        var accept = Ui.Button("Abrir projeto", () =>
        {
            try
            {
                string candidate = password.Password;
                result = DlbEncryptionKey.UnlockFile(path, candidate.AsSpan());
                if (remember.IsChecked == true)
                {
                    try { WindowsCredentialStore.SaveDlbPassword(path, candidate); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                        or System.Security.Cryptography.CryptographicException or Win32Exception)
                    {
                        reminderError = ex.Message;
                    }
                }
                else WindowsCredentialStore.RemoveDlbPassword(path);
                password.Clear();
                dialog.DialogResult = true;
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or ArgumentException)
            {
                error.Text = ex is CryptographicException ? "Senha incorreta ou arquivo DLB adulterado." : ex.Message;
                password.Clear(); password.Focus();
            }
        });
        accept.IsDefault = true; cancel.IsCancel = true;
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(cancel); actions.Children.Add(accept); content.Children.Add(actions);
        dialog.Loaded += (_, _) => password.Focus();
        bool accepted = dialog.ShowDialog() == true;
        if (accepted && reminderError != null)
            Ui.Alert("DendroLab", "Projeto desbloqueado, mas não foi possível lembrar a senha: " + reminderError, Ui.MessageTone.Warning);
        return accepted ? result : null;
    }
}
