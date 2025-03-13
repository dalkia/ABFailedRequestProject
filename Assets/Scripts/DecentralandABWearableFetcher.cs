using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Debug = UnityEngine.Debug;

public class DecentralandABWearableFetcher : MonoBehaviour
{
    private const string oldReferenceSnapshotContentURL = "https://peer.decentraland.org/content/contents/bafybeihdtxww224vlx7trjy4hj74aoqskdvbuajeuvlhlmbopz5hfdqaua";

    private const string snapshotURL = "https://peer.decentraland.org/content/snapshots";
    private const string contentsURL = "https://peer.decentraland.org/content/contents/";
    private const string manifestUrlTemplate = "https://ab-cdn.decentraland.org/manifest/{0}_windows.json";
    private const string assetBundleUrlTemplate = "https://ab-cdn.decentraland.org/{0}/{1}";
    private const string cacheFilePath = "AssetBundleCache.json"; // Path to save the cache file

    private CancellationTokenSource ct;
    private Dictionary<Hash128, string> assetBundleCache = new Dictionary<Hash128, string>(); // Cache for storing URL and hash

    private int totalFilesDownloaded;
    private int totalFilesToDownload = 13_000;
    void Start()
    {
        ct = new CancellationTokenSource();

        
        FetchAssetBundles().Forget();
    }
    
    private void OnDestroy()
    {
        StopDownloads();
    }
    
    private void StopDownloads()
    {
        SaveCache();
        ct.Cancel();
        Debug.Log($"DOWNLOAD FINISHED FOR {totalFilesDownloaded}");
    }

    private async UniTaskVoid FetchAssetBundles()
    {
        using UnityWebRequest request = UnityWebRequest.Get("google.com");
        await CompleteWebRequest(request);
        Debug.Log("Completed www.google.com");
        try
        {
            Debug.Log($"Getting Snapshot");
            string catalystSnapshotURL = await GetHashWithMostEntitiesAsync();
            Debug.Log($"Getting Catalyst Content");
            (bool, string) catalystResult;
            using (UnityWebRequest webRequest = UnityWebRequest.Get(catalystSnapshotURL))
            {
                catalystResult = await CompleteWebRequest(webRequest);
            }
            Debug.Log($"Starting AB download");
            if (catalystResult.Item1)
            {
                List<string> entityIds = ExtractEntityIds(catalystResult.Item2);
                List<UniTask> entityTasks = new List<UniTask>();
                int batchSize = 20;
                for (int i = 0; i < entityIds.Count; i++)
                {
                    await FetchEntityDataAsync(entityIds[i]);
                    entityTasks.Add(FetchEntityDataAsync(entityIds[i]));
                    // When we reach the batch size or the end of the list, wait for the tasks to complete
                    if (entityTasks.Count == batchSize || i == entityIds.Count - 1)
                    {
                        await UniTask.WhenAll(entityTasks);
                        entityTasks.Clear(); // Clear the list to start the next batch
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error: {ex.Message}");
        }
    }
    
    public async UniTask<string> GetHashWithMostEntitiesAsync()
    {
        // Assuming you're getting a list of hashes and their corresponding entity counts from a URL
        (bool, string) snapshotResult;
        using (UnityWebRequest webRequest = UnityWebRequest.Get(snapshotURL))
        {
            snapshotResult = await CompleteWebRequest(webRequest);
        }
        
        List<SnapshotData> snapshots = JsonConvert.DeserializeObject<List<SnapshotData>>(snapshotResult.Item2);

        if (snapshots == null || snapshots.Count == 0)
        {
            Debug.LogError("No entity data available");
            return null;
        }
        
        foreach (var snapshotData in snapshots)
        {
            if (snapshotData.numberOfEntities > 19000 && snapshotData.numberOfEntities < 22000)
            {
                return $"{contentsURL}{snapshotData.hash}";
            }
        }
        
        var snapshotWithMostEntities = snapshots
            .OrderByDescending(snapshot => snapshot.numberOfEntities)
            .First();

        //Just in case the number of entities drastically change during the test, lets have a fallback
        return $"{contentsURL}{snapshotWithMostEntities.hash}";
    }


    private async UniTask FetchEntityDataAsync(string entityId)
    {
        if (ct.IsCancellationRequested)
            return;
        
        string entityUrl = contentsURL + entityId;

        // Assuming you're getting a list of hashes and their corresponding entity counts from a URL
        (bool, string) entityDataResult;
        using (UnityWebRequest webRequest = UnityWebRequest.Get(entityUrl))
        {
            entityDataResult = await CompleteWebRequest(webRequest);
        }
        
        if (entityDataResult.Item1)
        {
            var entity = JsonUtility.FromJson<DecentralandEntity>(entityDataResult.Item2);

            if (entity.type == "wearable")
                await FetchManifestAndDownloadAssets(entityId);
            else
                Debug.Log($"{entityId} not a wearable, returning");
        }
    }

    private async UniTask FetchManifestAndDownloadAssets(string entityId)
    {
        if (ct.IsCancellationRequested)
            return;
        
        string manifestUrl = string.Format(manifestUrlTemplate, entityId);
        Debug.Log($"Downloading manifest: {manifestUrl}");

        (bool, string) manifestResult;
        using (UnityWebRequest request = UnityWebRequest.Get(manifestUrl))
        {
            manifestResult = await CompleteWebRequest(request);
        }

        if (manifestResult.Item1)
        {
            Debug.Log($"Manifest downloaded: {manifestUrl}");
            DecentralandManifest manifest = JsonUtility.FromJson<DecentralandManifest>(manifestResult.Item2);
            //Any version greater than this needs the hash in the url. For simplicity on building the url, we will ignore it
            if (int.Parse(manifest.version[1..]) >= 25)
            {
                Debug.Log($"Ignored due to version being greater than 25: {manifestUrl}");
                return;
            }
            List<UniTask> downloadAssetBundleTask = new List<UniTask>();
            foreach (var file in manifest.files)
            {
                if (!file.EndsWith("windows"))
                    continue;
                string assetBundleUrl = string.Format(assetBundleUrlTemplate, manifest.version, file);
                Hash128 hash = ComputeHash(manifest.version, file);
                if (!assetBundleCache.ContainsKey(hash))
                    downloadAssetBundleTask.Add(DownloadAssetBundleAsync(hash, assetBundleUrl));
            }
            
            await UniTask.WhenAll(downloadAssetBundleTask);
        }
        else
        {
            Debug.Log($"Manifest download failed: {manifestUrl}");
        }
    }

    public unsafe Hash128 ComputeHash(string version, string hash)
    {
        Span<char> hashBuilder = stackalloc char[version.Length + hash.Length];
        version.AsSpan().CopyTo(hashBuilder);
        hash.AsSpan().CopyTo(hashBuilder[version.Length..]);

        fixed (char* ptr = hashBuilder) { return Hash128.Compute(ptr, (uint)(sizeof(char) * hashBuilder.Length)); }
    }

    private int activeDownloads = 0;
    private int maximumAmountOfDownloads = 5;

    private async UniTask<(bool, string)> CompleteWebRequest(UnityWebRequest request, bool getTextResult = true)
    {
        try
        {
            while (activeDownloads >= maximumAmountOfDownloads)
                await UniTask.Yield();

            activeDownloads++;
            Debug.Log($"CURRENT ACTIVE DOWNLOADS {activeDownloads}");
            await request.SendWebRequest().ToUniTask();

            if (request.result == UnityWebRequest.Result.ConnectionError || request.result == UnityWebRequest.Result.ProtocolError)
            {
                Debug.LogError($"Request error: {request.error} {request.url}");
                return (false, null);
            }

            return (true, getTextResult ? request.downloadHandler.text : "");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Request failed: {ex.Message} {request.url}");
            return (false, null);
        }
        finally
        {
            activeDownloads--;
        }
    }
    
    
    private async UniTask DownloadAssetBundleAsync(Hash128 hash, string assetBundleUrl)
    {
        if (ct.IsCancellationRequested)
            return;

        if (Caching.IsVersionCached(assetBundleUrl, hash))
            return;

        using UnityWebRequest request = UnityWebRequestAssetBundle.GetAssetBundle(assetBundleUrl, hash);

        (bool, string) assetBundleResult = await CompleteWebRequest(request, false);
        
        if (assetBundleResult.Item1)
        {
            Debug.Log($"Successfully downloaded AssetBundle from {assetBundleUrl} {totalFilesDownloaded}");
            assetBundleCache[hash] = assetBundleUrl; // Save the hash and URL in the cache after successful download
            totalFilesDownloaded++;
            if(totalFilesDownloaded >= totalFilesToDownload)
                StopDownloads();
            //AssetBundle bundle = DownloadHandlerAssetBundle.GetContent(request);
        }
    }



    private List<string> ExtractEntityIds(string text)
    {
        List<string> entityIds = new List<string>();

        Regex regex = new Regex("\"entityId\":\"(.*?)\"");
        MatchCollection matches = regex.Matches(text);

        foreach (Match match in matches)
        {
            string entityId = match.Groups[1].Value;
            entityIds.Add(entityId);
        }

        return entityIds;
    }



    private void SaveCache()
    {
        try
        {
            string json = JsonConvert.SerializeObject(assetBundleCache, Formatting.Indented); // Use Newtonsoft.Json to serialize
            File.WriteAllText(Path.Combine(Application.dataPath, cacheFilePath), json);
            Debug.Log("Cache saved successfully.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to save cache: {ex.Message}");
        }
    }

    [Serializable]
    public class DecentralandEntity
    {
        public string type;
    }

    [Serializable]
    public class DecentralandManifest
    {
        public string version;
        public List<string> files;
    }
    
    [Serializable]
    public class SnapshotData
    {
        public string hash;
        public int numberOfEntities;
    }
}