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
    private string _id = string.Empty;
    private int _trackNumber;
    private string _title = string.Empty;
    private string _artist = string.Empty;
    private string _album = string.Empty;
    private string _durationString = "3:30";
    private string _imageUrl = string.Empty;
    private bool _isSelected = true;

    public string Id
    {
        get => _id;
        set { if (_id != value) { _id = value; OnPropertyChanged(); } }
    }

    public int TrackNumber
    {
        get => _trackNumber;
        set { if (_trackNumber != value) { _trackNumber = value; OnPropertyChanged(); } }
    }

    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; OnPropertyChanged(); } }
    }

    public string Artist
    {
        get => _artist;
        set { if (_artist != value) { _artist = value; OnPropertyChanged(); } }
    }

    public string Album
    {
        get => _album;
        set { if (_album != value) { _album = value; OnPropertyChanged(); } }
    }

    public string DurationString
    {
        get => _durationString;
        set { if (_durationString != value) { _durationString = value; OnPropertyChanged(); } }
    }

    public string ImageUrl
    {
        get => _imageUrl;
        set { if (_imageUrl != value) { _imageUrl = value; OnPropertyChanged(); } }
    }

    public string SpotifyUrl => $"https://open.spotify.com/track/{Id}";

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}