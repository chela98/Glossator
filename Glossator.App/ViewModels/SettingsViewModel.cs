using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Glossator.AI.Configuration;
using Glossator.AI.Prompts;
using Glossator.AI.Services;
using Glossator.App.Infrastructure;
using Glossator.App.Services;
using Wpf.Ui.Controls;

namespace Glossator.App.ViewModels;

public enum SettingsSubTab
{
    ProvidersAndAgents,
    Prompts
}

/// <summary>
/// Tek bir yapay zekâ ajanı rolünün sağlayıcı, model ve akıl yürütme yoğunluğu satırını temsil eder.
/// </summary>
public sealed class AgentConfigItemViewModel : ObservableObject
{
    private readonly Action _onDirty;
    private readonly Action<AgentConfigItemViewModel>? _onProviderChanged;
    private string _provider = SettingsViewModel.FollowActiveProviderLabel;
    private string _modelId = string.Empty;
    private string _reasoningEffort = "(Varsayılan)";

    public AgentConfigItemViewModel(
        string roleKey,
        string displayName,
        string roleDescription,
        Action onDirty,
        Action<AgentConfigItemViewModel>? onProviderChanged = null)
    {
        RoleKey = roleKey;
        DisplayName = displayName;
        RoleDescription = roleDescription;
        _onDirty = onDirty;
        _onProviderChanged = onProviderChanged;
    }

    public string RoleKey { get; }
    public string DisplayName { get; }
    public string RoleDescription { get; }
    public ObservableCollection<string> AvailableModels { get; } = [];

    public string Provider
    {
        get => _provider;
        set
        {
            if (SetProperty(ref _provider, value))
            {
                _onDirty();
                _onProviderChanged?.Invoke(this);
            }
        }
    }

    public string ModelId
    {
        get => _modelId;
        set
        {
            if (SetProperty(ref _modelId, value))
                _onDirty();
        }
    }

    internal void SetModelIdSilently(string value)
    {
        _modelId = value;
        OnPropertyChanged(nameof(ModelId));
    }

    public string ReasoningEffort
    {
        get => _reasoningEffort;
        set
        {
            if (SetProperty(ref _reasoningEffort, value))
                _onDirty();
        }
    }
}

/// <summary>
/// Sağlayıcı profilleri, ajan rol atamaları ve sistem prompt editörünü yönetir.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    public const string FollowActiveProviderLabel = "(Aktif Sağlayıcıyı İzle)";

    private readonly UserSettingsService _settingsService;
    private readonly IStudioDialogService _dialogService;
    private readonly BannerService _bannerService;
    private readonly IProviderDiscoveryService _discoveryService;

    private readonly Dictionary<string, AiProviderOptions> _editingProviders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _providerModelsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Message, bool? Succeeded)> _providerStatusCache = new(StringComparer.OrdinalIgnoreCase);

    private SettingsSubTab _selectedSubTab = SettingsSubTab.ProvidersAndAgents;
    private bool _isLoadingUi;
    private bool _isLocked;
    private bool _isSettingsDirty;
    private bool _isPromptDirty;
    private bool _isTestingProvider;
    private string _providerStatusMessage = string.Empty;
    private bool? _providerStatusSucceeded;

    private string? _currentEditingProviderKey;
    private string? _selectedActiveProvider;
    private string? _selectedEditProvider;
    private string _editEndpoint = string.Empty;
    private string _editApiKey = string.Empty;
    private string _editDefaultModel = string.Empty;
    private string _newProviderName = string.Empty;

    private string _selectedPromptFile = "TranslatorPrompt.md";
    private string _promptContent = string.Empty;
    private string _promptSourceLabel = string.Empty;

    public SettingsViewModel(
        UserSettingsService settingsService,
        IStudioDialogService dialogService,
        BannerService bannerService,
        IProviderDiscoveryService? discoveryService = null)
    {
        _settingsService = settingsService;
        _dialogService = dialogService;
        _bannerService = bannerService;
        _discoveryService = discoveryService ?? new ProviderDiscoveryService();

        Agents =
        [
            new AgentConfigItemViewModel("StyleAnalyst", "StyleAnalyst (Üslup Analizi)", "Kitap tonu ve terminoloji rehberi çıkarır", MarkSettingsDirty, OnAgentProviderChanged),
            new AgentConfigItemViewModel("Translator", "Translator (Ana Çevirmen)", "Bölüm kesitlerini Türkçe Strict XHTML üretir", MarkSettingsDirty, OnAgentProviderChanged),
            new AgentConfigItemViewModel("Reviewer", "Reviewer (Denetçi / Kalite)", "Terim tutarlılığını denetler ve yama önerir", MarkSettingsDirty, OnAgentProviderChanged)
        ];

        AddProviderCommand = new RelayCommand(AddProvider, () => !IsLocked);
        DeleteProviderCommand = new AsyncRelayCommand(DeleteProviderAsync, () => !IsLocked);
        PingProviderCommand = new AsyncRelayCommand(PingCurrentProviderAsync, () => !IsLocked && !IsTestingProvider);
        FetchModelsCommand = new AsyncRelayCommand(FetchModelsForCurrentProviderAsync, () => !IsLocked && !IsTestingProvider);
        SaveAiSettingsCommand = new AsyncRelayCommand(SaveAiSettingsAsync, () => !IsLocked);
        SavePromptCommand = new AsyncRelayCommand(SavePromptAsync, () => !IsLocked);
        ResetPromptCommand = new AsyncRelayCommand(ResetPromptAsync, () => !IsLocked);
        OpenPromptsFolderCommand = new RelayCommand(OpenPromptsFolder);
    }

    public ObservableCollection<string> ProviderNames { get; } = [];
    public ObservableCollection<string> AgentProviderChoices { get; } = [];
    public ObservableCollection<string> EditProviderModels { get; } = [];
    public ObservableCollection<AgentConfigItemViewModel> Agents { get; }

    public ObservableCollection<string> ReasoningEfforts { get; } =
        ["(Varsayılan)", "none", "minimal", "low", "medium", "high", "xhigh"];
    public IReadOnlyList<string> PromptFiles { get; } = ["StyleAnalystPrompt.md", "TranslatorPrompt.md", "ReviewerPrompt.md"];

    public event EventHandler? ApiKeyReloaded;

    public SettingsSubTab SelectedSubTab
    {
        get => _selectedSubTab;
        set => SetProperty(ref _selectedSubTab, value);
    }

    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            if (SetProperty(ref _isLocked, value))
            {
                ((RelayCommand)AddProviderCommand).NotifyCanExecuteChanged();
                ((AsyncRelayCommand)DeleteProviderCommand).NotifyCanExecuteChanged();
                ((AsyncRelayCommand)PingProviderCommand).NotifyCanExecuteChanged();
                ((AsyncRelayCommand)FetchModelsCommand).NotifyCanExecuteChanged();
                ((AsyncRelayCommand)SaveAiSettingsCommand).NotifyCanExecuteChanged();
                ((AsyncRelayCommand)SavePromptCommand).NotifyCanExecuteChanged();
                ((AsyncRelayCommand)ResetPromptCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsTestingProvider
    {
        get => _isTestingProvider;
        private set
        {
            if (SetProperty(ref _isTestingProvider, value))
            {
                ((AsyncRelayCommand)PingProviderCommand).NotifyCanExecuteChanged();
                ((AsyncRelayCommand)FetchModelsCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public string ProviderStatusMessage
    {
        get => _providerStatusMessage;
        private set
        {
            if (SetProperty(ref _providerStatusMessage, value))
                OnPropertyChanged(nameof(HasProviderStatus));
        }
    }

    public bool? ProviderStatusSucceeded
    {
        get => _providerStatusSucceeded;
        private set => SetProperty(ref _providerStatusSucceeded, value);
    }

    public bool HasProviderStatus => !string.IsNullOrWhiteSpace(ProviderStatusMessage);

    public bool IsSettingsDirty
    {
        get => _isSettingsDirty;
        private set
        {
            if (SetProperty(ref _isSettingsDirty, value))
                OnPropertyChanged(nameof(SettingsDirtyBannerText));
        }
    }

    public bool IsPromptDirty
    {
        get => _isPromptDirty;
        private set => SetProperty(ref _isPromptDirty, value);
    }

    public string SettingsDirtyBannerText => IsSettingsDirty ? "Kaydedilmemiş değişiklik var" : "Tüm ayarlar güncel";

    public string? SelectedActiveProvider
    {
        get => _selectedActiveProvider;
        set
        {
            if (SetProperty(ref _selectedActiveProvider, value) && !_isLoadingUi)
            {
                MarkSettingsDirty();
                RefreshAllAvailableModelLists();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    SelectedEditProvider = value;
                }
            }
        }
    }

    public string? SelectedEditProvider
    {
        get => _selectedEditProvider;
        set
        {
            if (SetProperty(ref _selectedEditProvider, value) && !_isLoadingUi)
            {
                _isLoadingUi = true;
                try
                {
                    CommitCurrentEditingProvider();
                    LoadSelectedProviderFields(value);
                }
                finally
                {
                    _isLoadingUi = false;
                }
            }
        }
    }

    public string EditEndpoint
    {
        get => _editEndpoint;
        set
        {
            if (SetProperty(ref _editEndpoint, value) && !_isLoadingUi)
            {
                MarkSettingsDirty();
                ClearCurrentProviderStatus();
            }
        }
    }

    public string EditApiKey
    {
        get => _editApiKey;
        set
        {
            if (SetProperty(ref _editApiKey, value) && !_isLoadingUi)
            {
                MarkSettingsDirty();
                ClearCurrentProviderStatus();
            }
        }
    }

    public string EditDefaultModel
    {
        get => _editDefaultModel;
        set
        {
            if (SetProperty(ref _editDefaultModel, value) && !_isLoadingUi)
                MarkSettingsDirty();
        }
    }

    public string NewProviderName { get => _newProviderName; set => SetProperty(ref _newProviderName, value); }

    public string SelectedPromptFile
    {
        get => _selectedPromptFile;
        set => SetProperty(ref _selectedPromptFile, value);
    }

    public string PromptContent
    {
        get => _promptContent;
        set
        {
            if (SetProperty(ref _promptContent, value) && !_isLoadingUi)
                IsPromptDirty = true;
        }
    }

    public string PromptSourceLabel { get => _promptSourceLabel; private set => SetProperty(ref _promptSourceLabel, value); }

    public ICommand AddProviderCommand { get; }
    public ICommand DeleteProviderCommand { get; }
    public ICommand PingProviderCommand { get; }
    public ICommand FetchModelsCommand { get; }
    public ICommand SaveAiSettingsCommand { get; }
    public ICommand SavePromptCommand { get; }
    public ICommand ResetPromptCommand { get; }
    public ICommand OpenPromptsFolderCommand { get; }

    public void LoadFromSettings()
    {
        _isLoadingUi = true;
        try
        {
            var opts = _settingsService.CurrentOptions;
            _editingProviders.Clear();
            foreach (var (key, p) in opts.Providers)
            {
                _editingProviders[key] = new AiProviderOptions
                {
                    Endpoint = p.Endpoint,
                    ApiKey = p.ApiKey,
                    DefaultModel = p.DefaultModel
                };
            }

            if (_editingProviders.Count == 0)
            {
                PopulateDefaultPresetProviders(_editingProviders);
            }

            RefreshProviderLists(opts.ActiveProvider, opts.ActiveProvider);

            foreach (var agentVm in Agents)
            {
                opts.Agents.TryGetValue(agentVm.RoleKey, out var cfg);
                agentVm.Provider = string.IsNullOrWhiteSpace(cfg?.Provider)
                    ? FollowActiveProviderLabel
                    : (AgentProviderChoices.FirstOrDefault(p => p.Equals(cfg.Provider, StringComparison.OrdinalIgnoreCase)) ?? FollowActiveProviderLabel);
                agentVm.ModelId = cfg?.ModelId ?? string.Empty;

                var rawEffort = cfg?.ReasoningEffort?.Trim();
                if (string.IsNullOrWhiteSpace(rawEffort))
                {
                    agentVm.ReasoningEffort = "(Varsayılan)";
                }
                else
                {
                    var matchedEffort = ReasoningEfforts.FirstOrDefault(ef => ef.Equals(rawEffort, StringComparison.OrdinalIgnoreCase));
                    if (matchedEffort == null)
                    {
                        ReasoningEfforts.Add(rawEffort);
                        matchedEffort = rawEffort;
                    }

                    agentVm.ReasoningEffort = matchedEffort;
                }
            }

            RefreshAllAvailableModelLists();
            IsSettingsDirty = false;
        }
        finally
        {
            _isLoadingUi = false;
        }
    }

    public async Task InitializeAsync()
    {
        LoadFromSettings();
        await LoadSelectedPromptAsync();
    }

    public async Task<bool> TrySwitchPromptAsync(string nextFileName)
    {
        if (string.IsNullOrWhiteSpace(nextFileName) || nextFileName == _selectedPromptFile)
            return true;

        if (IsPromptDirty)
        {
            var proceed = await _dialogService.ConfirmAsync(
                "Kaydedilmemiş Prompt Değişikliği",
                "Üzerinde çalıştığınız prompt içeriğinde kaydedilmemiş değişiklikler var.\nKaydetmeden başka bir dosyaya geçmek istiyor musunuz?",
                "Evet, Değiştir");
            if (!proceed)
            {
                return false;
            }
        }

        _selectedPromptFile = nextFileName;
        OnPropertyChanged(nameof(SelectedPromptFile));
        await LoadSelectedPromptAsync();
        return true;
    }

    public bool ValidateActiveProviderKeys(out string message)
    {
        var opts = _settingsService.CurrentOptions;
        foreach (var role in new[] { "StyleAnalyst", "Translator", "Reviewer" })
        {
            var (providerName, providerOpts, _) = opts.ResolveAgent(role);
            bool isLocal = GlossatorAiOptions.IsLocalEndpoint(providerOpts.Endpoint);
            if (!isLocal && (string.IsNullOrWhiteSpace(providerOpts.ApiKey) || providerOpts.ApiKey.Contains("YOUR_", StringComparison.OrdinalIgnoreCase)))
            {
                message = $"'{role}' ajanı için '{providerName}' sağlayıcısının API anahtarı eksik.";
                return false;
            }
        }

        message = string.Empty;
        return true;
    }

    public async Task<bool> EnsureSettingsSavedBeforeRunAsync()
    {
        if (!IsSettingsDirty)
            return true;

        var save = await _dialogService.ConfirmAsync(
            "Kaydedilmemiş Ayarlar",
            "Ayarlar sekmesinde henüz kaydetmediğiniz sağlayıcı/model değişiklikleri var.\nÇeviriye başlamadan önce bu değişiklikler kaydedilsin mi?",
            "Kaydet ve Başlat",
            "Kaydetmeden Devam Et");

        if (save)
        {
            return await SaveAiSettingsAsync();
        }

        return true;
    }

    private void RefreshProviderLists(string? activeSelection, string? editSelection)
    {
        var savedAgentProviders = Agents.ToDictionary(a => a.RoleKey, a => a.Provider, StringComparer.OrdinalIgnoreCase);
        var names = _editingProviders.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

        ProviderNames.Clear();
        AgentProviderChoices.Clear();
        AgentProviderChoices.Add(FollowActiveProviderLabel);
        foreach (var n in names)
        {
            ProviderNames.Add(n);
            AgentProviderChoices.Add(n);
        }

        _selectedActiveProvider = names.FirstOrDefault(p => p.Equals(activeSelection, StringComparison.OrdinalIgnoreCase)) ?? names.FirstOrDefault();
        _selectedEditProvider = names.FirstOrDefault(p => p.Equals(editSelection, StringComparison.OrdinalIgnoreCase)) ?? _selectedActiveProvider;

        OnPropertyChanged(nameof(SelectedActiveProvider));
        OnPropertyChanged(nameof(SelectedEditProvider));
        LoadSelectedProviderFields(_selectedEditProvider);

        foreach (var ag in Agents)
        {
            var prev = savedAgentProviders.GetValueOrDefault(ag.RoleKey, FollowActiveProviderLabel);
            ag.Provider = AgentProviderChoices.FirstOrDefault(c => c.Equals(prev, StringComparison.OrdinalIgnoreCase))
                          ?? FollowActiveProviderLabel;
        }
    }

    private void CommitCurrentEditingProvider()
    {
        if (!string.IsNullOrWhiteSpace(_currentEditingProviderKey) &&
            _editingProviders.TryGetValue(_currentEditingProviderKey, out var p))
        {
            p.Endpoint = EditEndpoint.Trim();
            p.ApiKey = EditApiKey.Trim();
            p.DefaultModel = EditDefaultModel.Trim();
        }
    }

    private void LoadSelectedProviderFields(string? providerKey)
    {
        _currentEditingProviderKey = providerKey;
        if (!string.IsNullOrWhiteSpace(providerKey) && _editingProviders.TryGetValue(providerKey, out var p))
        {
            EditEndpoint = p.Endpoint;
            EditApiKey = p.ApiKey;
            EditDefaultModel = p.DefaultModel;
            ApiKeyReloaded?.Invoke(this, EventArgs.Empty);

            if (_providerStatusCache.TryGetValue(providerKey, out var cachedStatus))
            {
                ProviderStatusMessage = cachedStatus.Message;
                ProviderStatusSucceeded = cachedStatus.Succeeded;
            }
            else
            {
                ProviderStatusMessage = string.Empty;
                ProviderStatusSucceeded = null;
            }
        }
        else
        {
            ProviderStatusMessage = string.Empty;
            ProviderStatusSucceeded = null;
        }

        RefreshEditProviderModels();
    }

    private static void PopulateDefaultPresetProviders(IDictionary<string, AiProviderOptions> target)
    {
        target["Evren"] = new AiProviderOptions
        {
            Endpoint = "https://evren-llmapi.ssyz.org.tr/v1",
            ApiKey = string.Empty,
            DefaultModel = "deepseek-v4.1-flash"
        };
        target["Gemini"] = new AiProviderOptions
        {
            Endpoint = "https://generativelanguage.googleapis.com/v1beta/openai/",
            ApiKey = string.Empty,
            DefaultModel = "gemini-2.5-flash"
        };
        target["OpenAI"] = new AiProviderOptions
        {
            Endpoint = "https://api.openai.com/v1",
            ApiKey = string.Empty,
            DefaultModel = "gpt-4o"
        };
        target["Ollama"] = new AiProviderOptions
        {
            Endpoint = "http://localhost:11434/v1",
            ApiKey = "ollama",
            DefaultModel = "qwen2.5:14b"
        };
    }

    private void AddProvider()
    {
        var name = NewProviderName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            _bannerService.Show("Sağlayıcı Adı Eksik", "Eklemek istediğiniz sağlayıcı için bir ad yazın (ör: Ollama, Groq, OpenRouter).", InfoBarSeverity.Warning);
            return;
        }

        if (_editingProviders.ContainsKey(name))
        {
            _bannerService.Show("Zaten Mevcut", $"'{name}' adlı sağlayıcı zaten listede.", InfoBarSeverity.Warning);
            return;
        }

        _isLoadingUi = true;
        try
        {
            CommitCurrentEditingProvider();
            _editingProviders[name] = new AiProviderOptions
            {
                Endpoint = "http://localhost:11434/v1",
                ApiKey = "ollama",
                DefaultModel = "qwen2.5:14b"
            };

            NewProviderName = string.Empty;
            RefreshProviderLists(SelectedActiveProvider, name);
            RefreshAllAvailableModelLists();
            MarkSettingsDirty();
        }
        finally
        {
            _isLoadingUi = false;
        }

        _bannerService.Show("Sağlayıcı Eklendi", $"'{name}' eklendi. Endpoint ve model bilgilerini doldurup 'Ayarları Kaydet' butonuna basın.", InfoBarSeverity.Success);
    }

    private async Task DeleteProviderAsync()
    {
        var key = SelectedEditProvider;
        if (string.IsNullOrWhiteSpace(key)) return;

        if (_editingProviders.Count <= 1)
        {
            _bannerService.Show("Silinemez", "Sistemde en az bir sağlayıcı kalmalıdır.", InfoBarSeverity.Warning);
            return;
        }

        if (!await _dialogService.ConfirmAsync(
                "Sağlayıcıyı Sil",
                $"'{key}' sağlayıcısı listeden kaldırılsın mı?\n(Kalıcı olması için sonrasında 'Ayarları Kaydet'e tıklayın.)",
                "Evet, Sil"))
        {
            return;
        }

        _isLoadingUi = true;
        try
        {
            _editingProviders.Remove(key);
            _providerModelsCache.Remove(key);
            _providerStatusCache.Remove(key);
            _currentEditingProviderKey = null;
            var nextActive = string.Equals(SelectedActiveProvider, key, StringComparison.OrdinalIgnoreCase)
                ? _editingProviders.Keys.First()
                : SelectedActiveProvider;
            RefreshProviderLists(nextActive, nextActive);
            RefreshAllAvailableModelLists();
            MarkSettingsDirty();
        }
        finally
        {
            _isLoadingUi = false;
        }

        _bannerService.Show("Sağlayıcı Kaldırıldı", $"'{key}' listeden çıkarıldı. Kalıcı olması için 'Ayarları Kaydet'e basın.", InfoBarSeverity.Informational);
    }

    public async Task<bool> SaveAiSettingsAsync()
    {
        CommitCurrentEditingProvider();

        foreach (var (name, prov) in _editingProviders)
        {
            if (!string.IsNullOrWhiteSpace(prov.Endpoint))
            {
                if (!Uri.TryCreate(prov.Endpoint, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    _bannerService.Show(
                        "Geçersiz Endpoint URL",
                        $"'{name}' sağlayıcısının Endpoint adresi geçerli bir http:// veya https:// URL olmalıdır.",
                        InfoBarSeverity.Warning);
                    return false;
                }
            }
        }

        var activeProvider = SelectedActiveProvider ?? _editingProviders.Keys.First();
        var agentsDict = new Dictionary<string, AgentRoleOptions>(StringComparer.OrdinalIgnoreCase);

        foreach (var ag in Agents)
        {
            var provider = string.Equals(ag.Provider, FollowActiveProviderLabel, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : ag.Provider;
            var effort = ag.ReasoningEffort == "(Varsayılan)" ? string.Empty : ag.ReasoningEffort;

            agentsDict[ag.RoleKey] = new AgentRoleOptions
            {
                Provider = provider,
                ModelId = ag.ModelId.Trim(),
                ReasoningEffort = effort
            };
        }

        try
        {
            await _settingsService.SaveAiSettingsAsync(activeProvider, _editingProviders, agentsDict);
            IsSettingsDirty = false;
            _bannerService.Show("Ayarlar Kaydedildi", "API anahtarı, sağlayıcı ve model ayarları anında etkinleştirildi (~/.glossator/user_settings.json).", InfoBarSeverity.Success);
            return true;
        }
        catch (Exception ex)
        {
            _bannerService.Show("Ayarlar Kaydedilemedi", ex.Message, InfoBarSeverity.Error);
            return false;
        }
    }

    public async Task PingCurrentProviderAsync()
    {
        var providerKey = SelectedEditProvider ?? _currentEditingProviderKey;
        if (string.IsNullOrWhiteSpace(providerKey))
            return;

        var endpoint = EditEndpoint;
        var apiKey = EditApiKey;
        var defaultModel = EditDefaultModel;

        IsTestingProvider = true;
        ApplyProviderStatus(providerKey, "Bağlantı ve gecikme test ediliyor...", succeeded: null);

        try
        {
            var result = await _discoveryService.PingAsync(endpoint, apiKey, defaultModel);
            if (result.DiscoveredModels.Count > 0)
            {
                _providerModelsCache[providerKey] = result.DiscoveredModels.ToList();
                RefreshAllAvailableModelLists();
            }

            ApplyProviderStatus(providerKey, result.Message, result.Success);
        }
        catch (Exception ex)
        {
            ApplyProviderStatus(providerKey, $"Test sırasında hata: {ex.Message}", succeeded: false);
        }
        finally
        {
            IsTestingProvider = false;
        }
    }

    public async Task FetchModelsForCurrentProviderAsync()
    {
        var providerKey = SelectedEditProvider ?? _currentEditingProviderKey;
        if (string.IsNullOrWhiteSpace(providerKey))
            return;

        var endpoint = EditEndpoint;
        var apiKey = EditApiKey;

        IsTestingProvider = true;
        ApplyProviderStatus(providerKey, "Mevcut modeller çekiliyor...", succeeded: null);

        try
        {
            var result = await _discoveryService.FetchModelsAsync(endpoint, apiKey);
            if (result.Success && result.Models.Count > 0)
            {
                _providerModelsCache[providerKey] = result.Models.ToList();
                RefreshAllAvailableModelLists();
            }

            ApplyProviderStatus(providerKey, result.Message, result.Success);
        }
        catch (Exception ex)
        {
            ApplyProviderStatus(providerKey, $"Model listesi çekilemedi: {ex.Message}", succeeded: false);
        }
        finally
        {
            IsTestingProvider = false;
        }
    }

    private void OnAgentProviderChanged(AgentConfigItemViewModel agent)
    {
        RefreshAgentModels(agent);
    }

    private void RefreshAllAvailableModelLists()
    {
        RefreshEditProviderModels();
        foreach (var agent in Agents)
        {
            RefreshAgentModels(agent);
        }
    }

    private void RefreshEditProviderModels()
    {
        var models = GetCachedModelsForProvider(SelectedEditProvider);
        ReplaceModelsPreservingText(
            EditProviderModels,
            models,
            EditDefaultModel,
            preserved =>
            {
                _editDefaultModel = preserved;
                OnPropertyChanged(nameof(EditDefaultModel));
            });
    }

    private void RefreshAgentModels(AgentConfigItemViewModel agent)
    {
        var effectiveProvider = ResolveEffectiveProviderKey(agent.Provider);
        var models = GetCachedModelsForProvider(effectiveProvider);
        ReplaceModelsPreservingText(
            agent.AvailableModels,
            models,
            agent.ModelId,
            preserved => agent.SetModelIdSilently(preserved));
    }

    private IReadOnlyList<string> GetCachedModelsForProvider(string? providerKey)
    {
        if (!string.IsNullOrWhiteSpace(providerKey) &&
            _providerModelsCache.TryGetValue(providerKey, out var cached))
        {
            return cached;
        }

        return [];
    }

    private string? ResolveEffectiveProviderKey(string? agentProviderChoice)
    {
        if (string.IsNullOrWhiteSpace(agentProviderChoice) ||
            string.Equals(agentProviderChoice, FollowActiveProviderLabel, StringComparison.OrdinalIgnoreCase))
        {
            return SelectedActiveProvider;
        }

        return agentProviderChoice;
    }

    private void ReplaceModelsPreservingText(
        ObservableCollection<string> target,
        IReadOnlyList<string> source,
        string currentText,
        Action<string> restoreText)
    {
        var wasLoading = _isLoadingUi;
        _isLoadingUi = true;
        try
        {
            target.Clear();
            foreach (var model in source)
            {
                target.Add(model);
            }

            restoreText(currentText);
        }
        finally
        {
            _isLoadingUi = wasLoading;
        }
    }

    private void ApplyProviderStatus(string providerKey, string message, bool? succeeded)
    {
        _providerStatusCache[providerKey] = (message, succeeded);
        if (string.Equals(SelectedEditProvider, providerKey, StringComparison.OrdinalIgnoreCase))
        {
            ProviderStatusMessage = message;
            ProviderStatusSucceeded = succeeded;
        }
    }

    private void ClearCurrentProviderStatus()
    {
        if (!string.IsNullOrWhiteSpace(SelectedEditProvider))
        {
            _providerStatusCache.Remove(SelectedEditProvider);
        }

        ProviderStatusMessage = string.Empty;
        ProviderStatusSucceeded = null;
    }

    public async Task LoadSelectedPromptAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedPromptFile)) return;

        try
        {
            _isLoadingUi = true;
            var content = await PromptLoader.LoadPromptAsync(SelectedPromptFile);
            PromptContent = content;

            var diskPath = PromptLoader.GetActiveDiskPath(SelectedPromptFile);
            PromptSourceLabel = diskPath != null
                ? $"Kaynak: Disk Üzerinden Özelleştirilmiş ({diskPath})"
                : "Kaynak: Gömülü Varsayılan (DLL Resource - düzenleyip kaydederseniz EXE yanında geçersiz kılar)";
            IsPromptDirty = false;
        }
        catch (Exception ex)
        {
            _bannerService.Show("Prompt Yüklenemedi", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _isLoadingUi = false;
        }
    }

    private async Task SavePromptAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedPromptFile)) return;

        try
        {
            var promptsDir = Path.Combine(AppContext.BaseDirectory, "Prompts");
            Directory.CreateDirectory(promptsDir);
            var targetPath = Path.Combine(promptsDir, SelectedPromptFile);
            await File.WriteAllTextAsync(targetPath, PromptContent);

            PromptSourceLabel = $"Kaynak: Disk Üzerinden Özelleştirilmiş ({targetPath})";
            IsPromptDirty = false;
            _bannerService.Show("Prompt Kaydedildi", $"'{SelectedPromptFile}' diske yazıldı ve sonraki çeviride geçerli olacak.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            _bannerService.Show("Prompt Kaydedilemedi", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task ResetPromptAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedPromptFile)) return;

        if (!await _dialogService.ConfirmAsync(
                "Promptu Varsayılana Sıfırla",
                $"'{SelectedPromptFile}' üzerindeki özel disk değişiklikleri silinip gömülü varsayılana dönülsün mü?",
                "Sıfırla"))
        {
            return;
        }

        try
        {
            foreach (var p in new[]
                     {
                         Path.Combine(AppContext.BaseDirectory, "Prompts", SelectedPromptFile),
                         Path.Combine(AppContext.BaseDirectory, SelectedPromptFile)
                     })
            {
                if (File.Exists(p)) File.Delete(p);
            }

            await LoadSelectedPromptAsync();
            _bannerService.Show("Varsayılana Sıfırlandı", $"'{SelectedPromptFile}' için disk özelleştirmesi kaldırıldı.", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            _bannerService.Show("Prompt Sıfırlanamadı", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OpenPromptsFolder()
    {
        try
        {
            var promptsDir = Path.Combine(AppContext.BaseDirectory, "Prompts");
            _dialogService.OpenFolderInExplorer(promptsDir);
        }
        catch (Exception ex)
        {
            _bannerService.Show("Klasör Açılamadı", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void MarkSettingsDirty()
    {
        if (!_isLoadingUi)
            IsSettingsDirty = true;
    }
}
