using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using Glossator.App.Services;
using Glossator.Core.Glossary;
using Glossator.Core.Models.Epub;
using Glossator.Core.Models.Glossary;
using Glossator.Core.Parsing;
using Glossator.Core.Serialization;
using Wpf.Ui.Controls;

namespace Glossator.App.ViewModels;

/// <summary>
/// Master terim sözlüğünün filtreleme, satır içi kaydetme, toplu işlem ve YAML içe/dışa aktarımını yönetir.
/// </summary>
public sealed class GlossaryViewModel : ObservableObject, IDisposable
{
    private readonly SqliteGlossaryRepository _repo;
    private readonly IGlossarySerializationService _glossarySerializer;
    private readonly IStudioDialogService _dialogService;
    private readonly BannerService _bannerService;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private bool _isLoading;
    private bool _isLocked;
    private string _searchText = string.Empty;
    private int _selectedPolicyFilterIndex;
    private bool _unreviewedOnly;

    private string _newEnglishTerm = string.Empty;
    private string _newTurkishTerm = string.Empty;
    private GlossingPolicy _newPolicy = GlossingPolicy.ProgressiveGloss;
    private GlossingPolicy _selectedBulkPolicy = GlossingPolicy.ProgressiveGloss;

    private string _glossaryCountText = "0 Terim";
    private string _saveStatusText = "Hazır.";
    private int _selectedTermsCount;
    private int _filteredTermsCount;

    private IReadOnlyList<GlossaryTermViewModel> _selectedTerms = [];

    public GlossaryViewModel(
        SqliteGlossaryRepository repo,
        IGlossarySerializationService glossarySerializer,
        IStudioDialogService dialogService,
        BannerService bannerService)
    {
        _repo = repo;
        _glossarySerializer = glossarySerializer;
        _dialogService = dialogService;
        _bannerService = bannerService;

        FilteredTerms = CollectionViewSource.GetDefaultView(Terms);
        FilteredTerms.Filter = FilterTerm;

        ClearFiltersCommand = new RelayCommand(ClearFilters);
        AddTermCommand = new AsyncRelayCommand(AddTermAsync, () => !IsLocked);
        BulkApproveCommand = new AsyncRelayCommand(BulkApproveAsync, () => !IsLocked && HasSelection);
        BulkApplyPolicyCommand = new AsyncRelayCommand(BulkApplyPolicyAsync, () => !IsLocked && HasSelection);
        BulkDeleteCommand = new AsyncRelayCommand(BulkDeleteAsync, () => !IsLocked && HasSelection);
        CleanupObsoleteCommand = new AsyncRelayCommand(CleanupObsoleteAsync, () => !IsLocked);
        ImportBookTermsCommand = new AsyncRelayCommand(ImportBookTermsAsync, () => !IsLocked);
        ImportYamlCommand = new AsyncRelayCommand(ImportYamlAsync, () => !IsLocked);
        ExportYamlCommand = new AsyncRelayCommand(ExportYamlAsync, () => !IsLocked);
        ReloadCommand = new AsyncRelayCommand(LoadAsync, () => !IsLocked);
    }

    public ObservableCollection<GlossaryTermViewModel> Terms { get; } = [];
    public ICollectionView FilteredTerms { get; }

    public IReadOnlyList<GlossingPolicy> PolicyValues { get; } = Enum.GetValues<GlossingPolicy>();

    public IReadOnlyList<string> PolicyFilterLabels { get; } = ["Tümü", .. Enum.GetNames<GlossingPolicy>()];

    public Func<(EpubPackage? Package, string? BookPath)>? ActiveBookProvider { get; set; }
    public Action<bool, string?>? BusyChanged { get; set; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                RefreshFilterAndCount();
        }
    }

    public int SelectedPolicyFilterIndex
    {
        get => _selectedPolicyFilterIndex;
        set
        {
            if (SetProperty(ref _selectedPolicyFilterIndex, value))
                RefreshFilterAndCount();
        }
    }

    public bool UnreviewedOnly
    {
        get => _unreviewedOnly;
        set
        {
            if (SetProperty(ref _unreviewedOnly, value))
                RefreshFilterAndCount();
        }
    }

    public string NewEnglishTerm { get => _newEnglishTerm; set => SetProperty(ref _newEnglishTerm, value); }
    public string NewTurkishTerm { get => _newTurkishTerm; set => SetProperty(ref _newTurkishTerm, value); }
    public GlossingPolicy NewPolicy { get => _newPolicy; set => SetProperty(ref _newPolicy, value); }
    public GlossingPolicy SelectedBulkPolicy { get => _selectedBulkPolicy; set => SetProperty(ref _selectedBulkPolicy, value); }

    public string GlossaryCountText { get => _glossaryCountText; private set => SetProperty(ref _glossaryCountText, value); }
    public string SaveStatusText { get => _saveStatusText; private set => SetProperty(ref _saveStatusText, value); }

    public int SelectedTermsCount
    {
        get => _selectedTermsCount;
        private set
        {
            if (SetProperty(ref _selectedTermsCount, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectionSummaryText));
                NotifySelectionCommands();
            }
        }
    }

    public int FilteredTermsCount
    {
        get => _filteredTermsCount;
        private set
        {
            if (SetProperty(ref _filteredTermsCount, value))
                OnPropertyChanged(nameof(HasNoFilteredTerms));
        }
    }

    public bool HasSelection => SelectedTermsCount > 0;
    public bool HasNoFilteredTerms => FilteredTermsCount == 0;
    public string SelectionSummaryText => $"{SelectedTermsCount} terim seçili";

    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            if (SetProperty(ref _isLocked, value))
                NotifyAllCommands();
        }
    }

    public ICommand ClearFiltersCommand { get; }
    public ICommand AddTermCommand { get; }
    public ICommand BulkApproveCommand { get; }
    public ICommand BulkApplyPolicyCommand { get; }
    public ICommand BulkDeleteCommand { get; }
    public ICommand CleanupObsoleteCommand { get; }
    public ICommand ImportBookTermsCommand { get; }
    public ICommand ImportYamlCommand { get; }
    public ICommand ExportYamlCommand { get; }
    public ICommand ReloadCommand { get; }

    public void UpdateSelection(IEnumerable<GlossaryTermViewModel> selected)
    {
        _selectedTerms = selected.ToList();
        SelectedTermsCount = _selectedTerms.Count;
    }

    public async Task LoadAsync()
    {
        _isLoading = true;
        try
        {
            foreach (var old in Terms)
            {
                old.PropertyChanged -= OnTermPropertyChanged;
            }
            Terms.Clear();

            var terms = await _repo.GetAllTermsAsync();
            foreach (var entry in terms)
            {
                var vm = GlossaryTermViewModel.FromModel(entry);
                vm.PropertyChanged += OnTermPropertyChanged;
                Terms.Add(vm);
            }

            RefreshFilterAndCount();
            SaveStatusText = $"Veritabanı yüklendi ({Terms.Count} kayıt).";
        }
        catch (Exception ex)
        {
            _bannerService.Show("Sözlük Yüklenemedi", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async void OnTermPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isLoading || sender is not GlossaryTermViewModel vm) return;

        try
        {
            await _saveLock.WaitAsync();
            try
            {
                if (vm.Id > 0)
                {
                    await _repo.UpdateTermManualAsync(vm.ToModel());
                }
                else
                {
                    await _repo.AddOrUpdateTermAsync(vm.ToModel());
                }

                UpdateCounts();
                SaveStatusText = $"Kaydedildi: {vm.EnglishTerm} -> {vm.TurkishTerm ?? "(boş)"}";
            }
            finally
            {
                _saveLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // İşlem iptal edildi
        }
        catch (Exception ex)
        {
            SaveStatusText = "Kayıt hatası oluştu.";
            _bannerService.Show("Terim Güncellenemedi", ex.Message, InfoBarSeverity.Error);
        }
    }

    private bool FilterTerm(object obj)
    {
        if (obj is not GlossaryTermViewModel term) return false;

        if (UnreviewedOnly && term.IsReviewed)
            return false;

        if (SelectedPolicyFilterIndex > 0 && SelectedPolicyFilterIndex < PolicyFilterLabels.Count)
        {
            var policyStr = PolicyFilterLabels[SelectedPolicyFilterIndex];
            if (!term.Policy.ToString().Equals(policyStr, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        var query = SearchText.Trim();
        if (string.IsNullOrWhiteSpace(query)) return true;

        return term.EnglishTerm.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrWhiteSpace(term.TurkishTerm) && term.TurkishTerm.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrWhiteSpace(term.EnglishDefinition) && term.EnglishDefinition.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrWhiteSpace(term.SourceBook) && term.SourceBook.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void ClearFilters()
    {
        SearchText = string.Empty;
        SelectedPolicyFilterIndex = 0;
        UnreviewedOnly = false;
    }

    private void RefreshFilterAndCount()
    {
        FilteredTerms.Refresh();
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        FilteredTermsCount = FilteredTerms.Cast<object>().Count();
        var reviewedCount = Terms.Count(t => t.IsReviewed);
        var unreviewedCount = Terms.Count - reviewedCount;
        GlossaryCountText = $"Gösterilen: {FilteredTermsCount} / {Terms.Count} Terim  |  Onaylı: {reviewedCount}  |  İncelenmemiş: {unreviewedCount}";
    }

    private async Task AddTermAsync()
    {
        var eng = NewEnglishTerm.Trim();
        var tr = NewTurkishTerm.Trim();
        if (string.IsNullOrWhiteSpace(eng))
        {
            _bannerService.Show("Eksik Terim", "Lütfen İngilizce terim adını yazın.", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            BusyChanged?.Invoke(true, "Terim veritabanına ekleniyor...");
            var entry = new GlossaryEntry
            {
                EnglishTerm = eng,
                TurkishTerm = string.IsNullOrWhiteSpace(tr) ? null : tr,
                Policy = NewPolicy,
                IsReviewed = true,
                SourceBook = ActiveBookProvider?.Invoke().Package?.Title ?? "Manuel Giriş"
            };

            await _repo.AddOrUpdateTermAsync(entry);
            await LoadAsync();

            NewEnglishTerm = string.Empty;
            NewTurkishTerm = string.Empty;
            SaveStatusText = $"Eklendi: {eng}";
            _bannerService.Show("Terim Eklendi", $"'{eng}' terimi onaylı olarak sözlüğe kaydedildi.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            _bannerService.Show("Terim Eklenemedi", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            BusyChanged?.Invoke(false, "Hazır.");
        }
    }

    private async Task BulkApproveAsync()
    {
        if (_selectedTerms.Count == 0) return;

        try
        {
            BusyChanged?.Invoke(true, null);
            await _repo.BatchUpdateManualAsync(_selectedTerms.Select(s => s.Id), isReviewed: true);
            _isLoading = true;
            try
            {
                foreach (var vm in _selectedTerms) vm.IsReviewed = true;
            }
            finally
            {
                _isLoading = false;
            }

            RefreshFilterAndCount();
            SaveStatusText = $"Toplu onaylandı: {_selectedTerms.Count} terim.";
            _bannerService.Show("Toplu Onay Tamamlandı", $"{_selectedTerms.Count} terim Onaylı olarak işaretlendi.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            _bannerService.Show("Toplu Onay Hatası", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            BusyChanged?.Invoke(false, null);
        }
    }

    private async Task BulkApplyPolicyAsync()
    {
        if (_selectedTerms.Count == 0) return;

        try
        {
            BusyChanged?.Invoke(true, null);
            await _repo.BatchUpdateManualAsync(_selectedTerms.Select(s => s.Id), policy: SelectedBulkPolicy);
            _isLoading = true;
            try
            {
                foreach (var vm in _selectedTerms) vm.Policy = SelectedBulkPolicy;
            }
            finally
            {
                _isLoading = false;
            }

            SaveStatusText = $"Toplu ilke atandı: {_selectedTerms.Count} terim -> {SelectedBulkPolicy}";
            _bannerService.Show("İlke Güncellendi", $"{_selectedTerms.Count} terimin ilkesi '{SelectedBulkPolicy}' yapıldı.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            _bannerService.Show("Toplu İlke Hatası", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            BusyChanged?.Invoke(false, null);
        }
    }

    private async Task BulkDeleteAsync()
    {
        if (_selectedTerms.Count == 0) return;

        var count = _selectedTerms.Count;
        if (!await _dialogService.ConfirmAsync(
                "Toplu Terim Silme",
                $"Seçili {count} terim sözlükten kalıcı olarak silinsin mi?",
                "Evet, Sil"))
        {
            return;
        }

        try
        {
            BusyChanged?.Invoke(true, null);
            var ids = _selectedTerms.Where(s => s.Id > 0).Select(s => s.Id).ToList();
            await _repo.DeleteTermsBatchAsync(ids);

            foreach (var vm in _selectedTerms.ToList())
            {
                vm.PropertyChanged -= OnTermPropertyChanged;
                Terms.Remove(vm);
            }

            UpdateSelection([]);
            UpdateCounts();
            SaveStatusText = $"Toplu silindi: {count} terim.";
            _bannerService.Show("Toplu Silme Tamamlandı", $"{count} terim kalıcı olarak silindi.", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            _bannerService.Show("Toplu Silme Hatası", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            BusyChanged?.Invoke(false, null);
        }
    }

    private async Task CleanupObsoleteAsync()
    {
        try
        {
            BusyChanged?.Invoke(true, "Eski ve hatalı otomatik kayıtlar temizleniyor...");
            int removed = await _repo.CleanupObsoleteTermsAsync();
            await LoadAsync();
            _bannerService.Show(
                "Sözlük Temizliği",
                removed > 0 ? $"{removed} hatalı/mükerrer otomatik terim temizlendi." : "Temizlenecek hatalı kayıt bulunamadı.",
                InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            _bannerService.Show("Temizlik Hatası", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            BusyChanged?.Invoke(false, "Hazır.");
        }
    }

    public async Task ImportBookTermsAsync()
    {
        var active = ActiveBookProvider?.Invoke();
        var package = active?.Package;
        if (package == null)
        {
            _bannerService.Show("Kitap Seçilmedi", "Terim tarayabilmek için önce Aktif Kitap Masası'ndan bir kitap açın.", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            BusyChanged?.Invoke(true, "Kitaptaki Glossary ve Index bölümleri taranıyor...");

            var extracted = await Task.Run(() =>
            {
                var found = new List<GlossaryEntry>();
                found.AddRange(EpubGlossaryReader.Read(package));
                found.AddRange(EpubIndexReader.Read(package));
                return found;
            });

            if (extracted.Count == 0)
            {
                _bannerService.Show("Terim Bulunamadı", "Bu kitapta otomatik çıkarılabilir Glossary veya Index terimi tespit edilemedi.", InfoBarSeverity.Informational);
                return;
            }

            if (await _dialogService.ConfirmAsync(
                    "Kitaptan Terim Tarama",
                    $"'{package.Title}' kitabından {extracted.Count} aday terim bulundu.\nVeritabanında henüz olmayanlar taslak (İncelenmemiş) olarak eklensin mi?",
                    "Evet, İçe Aktar"))
            {
                int added = await _repo.ImportTermsAsync(extracted, package.Title);
                await LoadAsync();
                _bannerService.Show(
                    "Taslak Terimler Eklendi",
                    $"{added} yeni terim veritabanına eklendi ({extracted.Count - added} terim zaten mevcuttu).",
                    InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            _bannerService.Show("Tarama Hatası", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            BusyChanged?.Invoke(false, "Hazır.");
        }
    }

    private async Task ImportYamlAsync()
    {
        var active = ActiveBookProvider?.Invoke();
        var initialDir = active?.Package?.OpfDirectory;
        var filePath = _dialogService.PickYamlOpenFile(initialDir);
        if (string.IsNullOrWhiteSpace(filePath)) return;

        try
        {
            BusyChanged?.Invoke(true, "YAML sözlüğü içe aktarılıyor...");
            var imported = await _glossarySerializer.ImportAsync(filePath);
            int upserted = 0;

            foreach (var entry in imported)
            {
                if (string.IsNullOrWhiteSpace(entry.EnglishTerm)) continue;

                var existing = await _repo.GetTermByEnglishAsync(entry.EnglishTerm);
                if (existing != null)
                {
                    existing.TurkishTerm = !string.IsNullOrWhiteSpace(entry.TurkishTerm) ? entry.TurkishTerm : existing.TurkishTerm;
                    existing.Policy = entry.Policy;
                    existing.IsReviewed = existing.IsReviewed || entry.IsReviewed;
                    if (!string.IsNullOrWhiteSpace(entry.SourceBook))
                        existing.SourceBook = entry.SourceBook;

                    await _repo.UpdateTermManualAsync(existing);
                }
                else
                {
                    await _repo.AddOrUpdateTermAsync(entry);
                }
                upserted++;
            }

            await LoadAsync();
            _bannerService.Show("YAML İçe Aktarıldı", $"{Path.GetFileName(filePath)} içinden {upserted} terim güncellendi/eklendi.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            _bannerService.Show("İçe Aktarma Hatası", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            BusyChanged?.Invoke(false, "Hazır.");
        }
    }

    private async Task ExportYamlAsync()
    {
        var active = ActiveBookProvider?.Invoke();
        var initialDir = active?.Package?.OpfDirectory;
        var filePath = _dialogService.PickYamlSaveFile("glossary.yaml", initialDir);
        if (string.IsNullOrWhiteSpace(filePath)) return;

        try
        {
            BusyChanged?.Invoke(true, "YAML sözlüğü dışa aktarılıyor...");
            var all = await _repo.GetAllTermsAsync();
            await _glossarySerializer.ExportAsync(all, filePath, SerializationFormat.Yaml);
            _bannerService.Show("YAML Dışa Aktarıldı", $"{all.Count} terim '{Path.GetFileName(filePath)}' dosyasına kaydedildi.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            _bannerService.Show("Dışa Aktarma Hatası", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            BusyChanged?.Invoke(false, "Hazır.");
        }
    }

    private void NotifySelectionCommands()
    {
        ((AsyncRelayCommand)BulkApproveCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)BulkApplyPolicyCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)BulkDeleteCommand).NotifyCanExecuteChanged();
    }

    private void NotifyAllCommands()
    {
        ((AsyncRelayCommand)AddTermCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)CleanupObsoleteCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)ImportBookTermsCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)ImportYamlCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)ExportYamlCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)ReloadCommand).NotifyCanExecuteChanged();
        NotifySelectionCommands();
    }

    public void Dispose()
    {
        foreach (var vm in Terms)
        {
            vm.PropertyChanged -= OnTermPropertyChanged;
        }
        _saveLock.Dispose();
    }
}
