using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ExportX.Models;

public class SpotifyUserProfile : INotifyPropertyChanged
{
    private string _id = string.Empty;
    private string _displayName = string.Empty;
    private string _email = string.Empty;
    private string _avatarUrl = string.Empty;
    private string _product = "free";
    private int _followersCount;
    private string _country = string.Empty;

    public string Id
    {
        get => _id;
        set { if (_id != value) { _id = value; OnPropertyChanged(); } }
    }

    public string DisplayName
    {
        get => _displayName;
        set { if (_displayName != value) { _displayName = value; OnPropertyChanged(); } }
    }

    public string Email
    {
        get => _email;
        set { if (_email != value) { _email = value; OnPropertyChanged(); } }
    }

    public string AvatarUrl
    {
        get => _avatarUrl;
        set { if (_avatarUrl != value) { _avatarUrl = value; OnPropertyChanged(); } }
    }

    public string Product
    {
        get => _product;
        set { if (_product != value) { _product = value; OnPropertyChanged(); } }
    }

    public int FollowersCount
    {
        get => _followersCount;
        set { if (_followersCount != value) { _followersCount = value; OnPropertyChanged(); } }
    }

    public string Country
    {
        get => _country;
        set { if (_country != value) { _country = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public class SpotifyPlaylistSummary : INotifyPropertyChanged
{
    private string _id = string.Empty;
    private string _name = string.Empty;
    private string _description = string.Empty;
    private string _ownerName = string.Empty;
    private string _imageUrl = string.Empty;
    private int _totalTracks;
    private bool _isPublic = true;

    public string Id
    {
        get => _id;
        set { if (_id != value) { _id = value; OnPropertyChanged(); } }
    }

    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnPropertyChanged(); } }
    }

    public string Description
    {
        get => _description;
        set { if (_description != value) { _description = value; OnPropertyChanged(); } }
    }

    public string OwnerName
    {
        get => _ownerName;
        set { if (_ownerName != value) { _ownerName = value; OnPropertyChanged(); } }
    }

    public string ImageUrl
    {
        get => _imageUrl;
        set { if (_imageUrl != value) { _imageUrl = value; OnPropertyChanged(); } }
    }

    public int TotalTracks
    {
        get => _totalTracks;
        set { if (_totalTracks != value) { _totalTracks = value; OnPropertyChanged(); } }
    }

    public bool IsPublic
    {
        get => _isPublic;
        set { if (_isPublic != value) { _isPublic = value; OnPropertyChanged(); } }
    }

    public string SpotifyUrl => $"https://open.spotify.com/playlist/{Id}";

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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