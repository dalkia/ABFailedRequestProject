using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

public class AssetBundleDownloadThroughList : MonoBehaviour
{
    public TextAsset urlsTextAsset; 
    public int maxSimultaneousRequests = 15; 
    //private SemaphoreSlim semaphore;

    private CancellationTokenSource cancellationToken;

    private int completedDownloads;
    private int currentConcurrentRequests;

    void Start()
    {
        if (urlsTextAsset != null)
        {
            cancellationToken = new CancellationTokenSource();
            //semaphore = new SemaphoreSlim(maxSimultaneousRequests);
            List<string> urls = urlsTextAsset.text.Split('\n').ToList();
            urls = urls.Distinct().ToList();
            LoadAllAssetBundlesAsync(urls).Forget();
        }
        else
        {
            Debug.LogError("No TextAsset assigned!");
        }
    }

    private void OnDestroy()
    {
        cancellationToken.Cancel();
    }

    private async UniTask LoadAllAssetBundlesAsync(List<string> urls)
    {
        List<UniTask> tasks = new List<UniTask>();
        int batchSize = 100;
        int currentBatchSize = 0;
        foreach (string url in urls)
        {
            tasks.Add(LoadAssetBundleAsync(url));
            currentBatchSize++;
            if (currentBatchSize >= batchSize)
            {
                Debug.Log("I WILL WaIT UNTIL THEY ARE ALL COMPLETE");
                await UniTask.WhenAll(tasks);
                currentBatchSize = 0;
                tasks.Clear();
            }
        }
        Debug.Log("COMPLETED REQUESTS");
    }

    private async UniTask LoadAssetBundleAsync(string url)
    {
        while (currentConcurrentRequests >= maxSimultaneousRequests)
            await UniTask.Yield();
        
        //await semaphore.WaitAsync();
        if (cancellationToken.IsCancellationRequested)
            return;
        
        currentConcurrentRequests++;
        try
        {
            Debug.Log($"CURRENT CONCURRENT DOWNLOADS {currentConcurrentRequests}");
            using UnityWebRequest webRequest = url.StartsWith("https://ab-cdn.decentraland.org/v") ? 
                UnityWebRequestAssetBundle.GetAssetBundle(url) : UnityWebRequest.Get(url) ;
            await webRequest.SendWebRequest().ToUniTask();
            if (webRequest.result == UnityWebRequest.Result.Success)
                Debug.Log($"Successfully complete request: {url} {completedDownloads}");
            else
                Debug.LogError("Failed to complete request: " + url + " Error: " + webRequest.error);

        }
        catch (UnityWebRequestException e)
        {
            Debug.LogError("Exception to completed request: " + url + " Error: " + e.Message);

        }
        finally
        {
            completedDownloads++;
            currentConcurrentRequests--;
            //semaphore.Release();
        }
    }
    
    public unsafe Hash128 ComputeHash(string version, string hash)
    {
        Span<char> hashBuilder = stackalloc char[version.Length + hash.Length];
        version.AsSpan().CopyTo(hashBuilder);
        hash.AsSpan().CopyTo(hashBuilder[version.Length..]);

        fixed (char* ptr = hashBuilder) { return Hash128.Compute(ptr, (uint)(sizeof(char) * hashBuilder.Length)); }
    }
}

