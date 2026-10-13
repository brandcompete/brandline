using System.Windows;
using Brandline.Models;
using Brandline.Services;
using Microsoft.Win32;

namespace Brandline.Views;

public partial class ContactWindow : Window
{
    private readonly Guid _id;
    private readonly string _originalAvatar;
    private string _avatar;

    public ContactWindow(Contact contact)
    {
        InitializeComponent();
        Icon = TrayIcon.CreateWindowIcon();
        _id = contact.Id;
        _originalAvatar = _avatar = contact.Avatar;
        NameBox.Text = contact.Name;
        CompanyBox.Text = contact.Company;
        NumberBox.Text = contact.Number;
        NoteBox.Text = contact.Note;
        MobileRadio.IsChecked = contact.NumberType == PhoneType.Mobile;
        LandlineRadio.IsChecked = contact.NumberType == PhoneType.Landline;
        UpdateAvatar();
        Loaded += (_, _) => NameBox.Focus();
        Closed += (_, _) =>
        {
            // Abgebrochen: neu gewähltes Bild wieder verwerfen.
            if (DialogResult != true && _avatar != _originalAvatar) Avatars.Delete(_avatar);
        };
    }

    /// <summary>Bearbeitete Kopie; das Original bleibt bei Abbruch unverändert.</summary>
    public Contact Contact { get; private set; } = new();

    private void UpdateAvatar()
    {
        AvatarPreview.DisplayName = NameBox.Text;
        AvatarPreview.Image = Avatars.Load(_avatar);
        RemoveAvatarButton.IsEnabled = _avatar.Length > 0;
    }

    private void OnNameChanged(object sender, RoutedEventArgs e) => UpdateAvatar();

    private void OnChooseAvatar(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Avatar wählen",
            Filter = "Bilder (*.jpg;*.jpeg;*.png;*.bmp;*.gif)|*.jpg;*.jpeg;*.png;*.bmp;*.gif",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var file = Avatars.Import(dialog.FileName, _id);
            if (_avatar != _originalAvatar) Avatars.Delete(_avatar);
            _avatar = file;
            UpdateAvatar();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Das Bild kann nicht gelesen werden: {ex.Message}", "Avatar",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnRemoveAvatar(object sender, RoutedEventArgs e)
    {
        if (_avatar != _originalAvatar) Avatars.Delete(_avatar);
        _avatar = "";
        UpdateAvatar();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text) || PhoneNumber.ForDialing(NumberBox.Text).Length == 0)
        {
            MessageBox.Show(this, "Bitte Name und Telefonnummer angeben.", "Kontakt",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Altes Bild aufräumen, wenn es ersetzt oder entfernt wurde.
        if (_originalAvatar.Length > 0 && _avatar != _originalAvatar) Avatars.Delete(_originalAvatar);

        Contact = new Contact
        {
            Id = _id,
            Name = NameBox.Text.Trim(),
            Company = CompanyBox.Text.Trim(),
            Number = NumberBox.Text.Trim(),
            NumberType = LandlineRadio.IsChecked == true ? PhoneType.Landline : PhoneType.Mobile,
            Note = NoteBox.Text.Trim(),
            Avatar = _avatar,
        };
        DialogResult = true;
    }
}
