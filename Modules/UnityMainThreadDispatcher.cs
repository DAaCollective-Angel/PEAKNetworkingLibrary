using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class UnityMainThreadDispatcher : MonoBehaviour
{
    static UnityMainThreadDispatcher? instance;
    static readonly Queue<Action> queue = new Queue<Action>();

    public static UnityMainThreadDispatcher Instance()
    {
        if (instance == null)
        {
            var go = GameObject.Find("UnityMainThreadDispatcher");
            if (go == null)
            {
                go = new GameObject("UnityMainThreadDispatcher");
                DontDestroyOnLoad(go);
                instance = go.AddComponent<UnityMainThreadDispatcher>();
            }
            else instance = go.GetComponent<UnityMainThreadDispatcher>() ?? go.AddComponent<UnityMainThreadDispatcher>();
        }
        return instance;
    }

    public void Enqueue(Action a, float delaySeconds = 0f)
    {
        if (delaySeconds <= 0f)
        {
            lock (queue) queue.Enqueue(a);
        }
        else StartCoroutine(EnqueueDelayed(a, delaySeconds));
    }

    IEnumerator EnqueueDelayed(Action a, float d)
    {
        yield return new WaitForSeconds(d);
        lock (queue) queue.Enqueue(a);
    }

    void Update()
    {
        while (true)
        {
            Action a = null!;
            lock (queue)
            {
                if (queue.Count > 0) a = queue.Dequeue();
                else break;
            }
            try { a?.Invoke(); } catch (Exception ex) { Debug.LogError($"Dispatcher action error: {ex}"); }
        }
    }
}
