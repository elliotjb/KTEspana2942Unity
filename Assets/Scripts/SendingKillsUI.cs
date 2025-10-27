using System;
using UnityEngine;
using Cysharp.Threading.Tasks;

public class SendingKillsUI : MonoBehaviour
{
    [SerializeField] private RectTransform progress;
    [SerializeField] private float speed = 100f; // píxeles por segundo
    [SerializeField] private float minX = -27f;
    [SerializeField] private float maxX = 165f;

    private void OnEnable()
    {
        StartA().Forget();
    }

    private async UniTaskVoid StartA()
    {
        Vector2 pos = progress.anchoredPosition;

        while (gameObject.activeSelf)
        {
            // Mover según el tiempo (deltaTime)
            pos.x += speed * Time.deltaTime;

            // Si llegó al máximo, reinicia al inicio
            if (pos.x >= maxX)
                pos.x = minX;

            // Aplicar nueva posición
            progress.anchoredPosition = pos;

            // Esperar al siguiente frame
            await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken: this.GetCancellationTokenOnDestroy());
        }
    }
}
