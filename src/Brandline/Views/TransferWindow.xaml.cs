using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brandline.Models;
using Brandline.Services;

namespace Brandline.Views;

/// <summary>Ziel für das Weiterleiten wählen: direkt (Blind Transfer) oder mit Rückfrage.</summary>
public partial class TransferWindow : Window
{
    public sealed record Match(string Name, string Number, string Detail, ImageSource? Image);

    private readonly IReadOnlyCollection<Contact> _contacts;

    public TransferWindow(IReadOnlyCollection<Contact> contacts, string currentCaller)
    {
        InitializeComponent();
        Icon = TrayIcon.CreateWindowIcon();
        _contacts = contacts;
        TitleText.Text = $"„{currentCaller}“ weiterleiten an …";
        Loaded += (_, _) => SearchBox.Focus();
        UpdateMatches();
    }

    /// <summary>Gewählte Zielnummer (nach „Direkt weiterleiten“ oder „Rückfrage“).</summary>
    public string? Number { get; private set; }

    /// <summary>True = Rückfrage (Ziel zuerst anrufen), false = direkt weiterleiten.</summary>
    public bool Consult { get; private set; }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => UpdateMatches();

    private void UpdateMatches()
    {
        var query = SearchBox.Text.Trim();
        var digits = PhoneNumber.ForDialing(query);
        Matches.ItemsSource = _contacts
            .SelectMany(c => c.Numbers.Select(n => (Contact: c, Number: n)))
            .Where(x => query.Length == 0
                        || x.Contact.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                        || x.Contact.Company.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                        || (digits.Length >= 2 && PhoneNumber.ForDialing(x.Number.Number).Contains(digits)))
            .OrderBy(x => x.Contact.Name)
            .Take(30)
            .Select(x => new Match(x.Contact.Name, x.Number.Number, $"{x.Number.Number} · {x.Number.TypeLabel}", x.Contact.AvatarImage))
            .ToList();
    }

    private void OnMatchSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Matches.SelectedItem is Match m)
        {
            SearchBox.TextChanged -= OnSearchChanged;
            SearchBox.Text = m.Number;
            SearchBox.TextChanged += OnSearchChanged;
        }
    }

    private void OnMatchDoubleClick(object sender, MouseButtonEventArgs e) => Finish(consult: false);

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && Matches.Items.Count > 0)
        {
            Matches.SelectedIndex = 0;
            (Matches.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
    }

    private void OnBlind(object sender, RoutedEventArgs e) => Finish(consult: false);
    private void OnConsult(object sender, RoutedEventArgs e) => Finish(consult: true);

    private void Finish(bool consult)
    {
        var number = PhoneNumber.ForDialing(SearchBox.Text).Length > 0 ? SearchBox.Text.Trim() : (Matches.SelectedItem as Match)?.Number;
        if (number == null || PhoneNumber.ForDialing(number).Length == 0)
        {
            MessageBox.Show(this, "Bitte eine Nummer eingeben oder einen Kontakt wählen.", "Weiterleiten",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Number = number;
        Consult = consult;
        DialogResult = true;
    }
}
