using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ExportX.Models;

public class SpotifyUserProfile
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string AvatarUrl { get; set; } = string.Empty;
    public string Product { get; set; } = "free"; // free or premium
    public int FollowersCount { get; set; }
    public string Country { get; set; } = string.Empty;
}

public class SpotifyPlaylistSummary
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public int TotalTracks { get; set; }
    public bool IsPublic { get; set; } = true;
    public string SpotifyUrl => $"https://open.spotify.com/playlist/{Id}";
}

public class SpotifyTrackItem : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public string Id { get; set; } = string.Empty;
    public int TrackNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string DurationString { get; set; } = "0:00";
    public string ImageUrl { get; set; } = string.Empty;
    public string SpotifyUrl => $"https://open.spotify.com/track/{Id}";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
