using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klydis.Core.RAG;
using Microsoft.Extensions.Logging;

namespace Klydis.App.ViewModels
{
    public partial class RagViewModel : ObservableObject, IDisposable
    {
        private readonly VectorStore _vectorStore;
        private readonly DocumentIngestionEngine _ingestionEngine;
        private readonly HybridRetriever _hybridRetriever;
        private readonly ILogger<RagViewModel>? _logger;
        private CancellationTokenSource? _indexingCts;

        [ObservableProperty]
        private string _selectedFolderPath = string.Empty;

        [ObservableProperty]
        private string _collectionName = string.Empty;

        [ObservableProperty]
        private bool _isIndexing;

        [ObservableProperty]
        private string _progressMessage = "Ready";

        [ObservableProperty]
        private double _indexingProgressPercentage;

        [ObservableProperty]
        private int _totalIndexedDocuments;

        [ObservableProperty]
        private int _totalVectorChunks;

        [ObservableProperty]
        private string _searchQuery = string.Empty;

        [ObservableProperty]
        private VectorCollectionRecord? _selectedCollection;

        public ObservableCollection<VectorCollectionRecord> Collections { get; } = new();
        public ObservableCollection<HybridSearchResult> SearchResults { get; } = new();

        public RagViewModel(
            VectorStore vectorStore,
            DocumentIngestionEngine ingestionEngine,
            HybridRetriever hybridRetriever,
            ILogger<RagViewModel>? logger = null)
        {
            _vectorStore = vectorStore;
            _ingestionEngine = ingestionEngine;
            _hybridRetriever = hybridRetriever;
            _logger = logger;

            _ingestionEngine.ProgressChanged += OnIngestionProgressChanged;
            Klydis.Core.Diagnostics.FireAndForget.Observe(LoadCollectionsAsync(), _logger, nameof(LoadCollectionsAsync));
        }

        private void OnIngestionProgressChanged(object? sender, IngestionProgressEventArgs e)
        {
            App.Current?.Dispatcher?.Invoke(() =>
            {
                if (e.TotalFiles > 0)
                {
                    IndexingProgressPercentage = ((double)e.ProcessedFiles / e.TotalFiles) * 100.0;
                }
                ProgressMessage = $"Indexing file {e.ProcessedFiles}/{e.TotalFiles}: {Path.GetFileName(e.CurrentFilePath)} ({e.TotalChunksCreated} chunks)";
                TotalVectorChunks = _vectorStore.GetTotalChunkCount();
            });
        }

        public async Task LoadCollectionsAsync()
        {
            try
            {
                await _vectorStore.InitializeAsync();
                var list = await _vectorStore.GetCollectionsAsync();

                Collections.Clear();
                foreach (var col in list)
                {
                    Collections.Add(col);
                }

                TotalVectorChunks = _vectorStore.GetTotalChunkCount();
                TotalIndexedDocuments = Collections.Count;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to load vector collections.");
            }
        }

        [RelayCommand]
        private void BrowseFolder()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select a workspace folder to index into Klydis Vector RAG"
            };

            if (dialog.ShowDialog() == true)
            {
                SelectedFolderPath = dialog.FolderName;
                if (string.IsNullOrWhiteSpace(CollectionName))
                {
                    CollectionName = Path.GetFileName(dialog.FolderName);
                }
            }
        }

        [RelayCommand]
        private async Task StartIndexingAsync()
        {
            if (string.IsNullOrWhiteSpace(SelectedFolderPath) || !Directory.Exists(SelectedFolderPath))
            {
                ProgressMessage = "Error: Invalid folder path.";
                return;
            }

            string name = string.IsNullOrWhiteSpace(CollectionName) ? Path.GetFileName(SelectedFolderPath) : CollectionName;
            IsIndexing = true;
            ProgressMessage = "Initializing vector indexer...";
            IndexingProgressPercentage = 0;

            _indexingCts = new CancellationTokenSource();

            try
            {
                var collection = await _vectorStore.AddOrUpdateCollectionAsync(
                    name: name,
                    folderPath: SelectedFolderPath,
                    embeddingModel: "LLamaEmbedder-Local",
                    dimension: 384
                );

                int chunksIndexed = await Task.Run(() => _ingestionEngine.IndexDirectoryAsync(
                    collectionId: collection.Id,
                    directoryPath: SelectedFolderPath,
                    cancellationToken: _indexingCts.Token
                ));

                ProgressMessage = $"Indexing complete! Created {chunksIndexed} vector chunks.";
                await LoadCollectionsAsync();
            }
            catch (OperationCanceledException)
            {
                ProgressMessage = "Indexing cancelled by user.";
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during document indexing.");
                ProgressMessage = $"Error: {ex.Message}";
            }
            finally
            {
                IsIndexing = false;
            }
        }

        [RelayCommand]
        private void CancelIndexing()
        {
            _indexingCts?.Cancel();
        }

        [RelayCommand]
        private async Task DeleteCollectionAsync(VectorCollectionRecord? collection)
        {
            if (collection == null) return;

            try
            {
                await _vectorStore.DeleteCollectionAsync(collection.Id);
                Collections.Remove(collection);
                TotalVectorChunks = _vectorStore.GetTotalChunkCount();
                TotalIndexedDocuments = Collections.Count;
                ProgressMessage = $"Deleted workspace index '{collection.Name}'.";
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to delete collection.");
            }
        }

        [RelayCommand]
        private async Task PerformTestSearchAsync()
        {
            if (string.IsNullOrWhiteSpace(SearchQuery)) return;

            try
            {
                string? filterId = SelectedCollection?.Id;
                var results = await _hybridRetriever.SearchAsync(SearchQuery, topK: 5, collectionIdFilter: filterId);

                SearchResults.Clear();
                foreach (var res in results)
                {
                    SearchResults.Add(res);
                }

                ProgressMessage = $"Found {results.Count} matching context chunks via RRF Hybrid Search.";
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during RAG test search.");
                ProgressMessage = $"Search error: {ex.Message}";
            }
        }

        public void Dispose()
        {
            _ingestionEngine.ProgressChanged -= OnIngestionProgressChanged;
            _indexingCts?.Dispose();
        }
    }
}
