using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using PixivRanker.Services;

namespace PixivRanker;

public partial class BlacklistWindow : Window
{
    public BlacklistWindow(
        IEnumerable<long> userIds,
        IReadOnlyDictionary<long, string> authorNames)
    {
        InitializeComponent();
        DataContext = this;

        foreach (var userId in userIds.Order())
        {
            authorNames.TryGetValue(userId, out var authorName);
            Entries.Add(new BlacklistEntry(userId, authorName ?? string.Empty));
        }

        UpdateCount();
        Loaded += (_, _) =>
        {
            ThemeManager.ApplyWindowChrome(this);
            UserIdTextBox.Focus();
        };
    }

    public ObservableCollection<BlacklistEntry> Entries { get; } = [];

    public IReadOnlyCollection<long> UserIds => Entries.Select(entry => entry.UserId).ToArray();

    public IReadOnlyDictionary<long, string> AuthorNames => Entries
        .Where(entry => !string.IsNullOrWhiteSpace(entry.AuthorName))
        .ToDictionary(entry => entry.UserId, entry => entry.AuthorName.Trim());

    private void AddButton_Click(object sender, RoutedEventArgs e) => AddEntry();

    private void UserIdTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddEntry();
            e.Handled = true;
        }
    }

    private void AddEntry()
    {
        if (!long.TryParse(UserIdTextBox.Text.Trim(), out var userId) || userId <= 0)
        {
            MessageBox.Show(this, "请输入有效的正整数 Pixiv 作者 ID。", "作者 ID 不正确",
                MessageBoxButton.OK, MessageBoxImage.Information);
            UserIdTextBox.Focus();
            UserIdTextBox.SelectAll();
            return;
        }

        var authorName = AuthorNameTextBox.Text.Trim();
        var existing = Entries.FirstOrDefault(entry => entry.UserId == userId);
        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(authorName) && existing.AuthorName != authorName)
            {
                var index = Entries.IndexOf(existing);
                Entries[index] = new BlacklistEntry(userId, authorName);
            }

            BlacklistListView.SelectedItem = Entries.First(entry => entry.UserId == userId);
            BlacklistListView.ScrollIntoView(BlacklistListView.SelectedItem);
        }
        else
        {
            var entry = new BlacklistEntry(userId, authorName);
            Entries.Add(entry);
            BlacklistListView.SelectedItem = entry;
            BlacklistListView.ScrollIntoView(entry);
        }

        UserIdTextBox.Clear();
        AuthorNameTextBox.Clear();
        UserIdTextBox.Focus();
        UpdateCount();
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = BlacklistListView.SelectedItems.Cast<BlacklistEntry>().ToArray();
        foreach (var entry in selected)
        {
            Entries.Remove(entry);
        }

        UpdateCount();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void UpdateCount()
    {
        CountTextBlock.Text = $"共 {Entries.Count} 位作者";
    }
}

public sealed record BlacklistEntry(long UserId, string AuthorName);
