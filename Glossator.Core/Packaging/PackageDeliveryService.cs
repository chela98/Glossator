namespace Glossator.Core.Packaging;

public sealed class PackageDeliveryService : IPackageDeliveryService
{
    public async Task DeliverAtomicallyAsync(string sourceStagingFile, string finalDestinationPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourceStagingFile))
        {
            throw new FileNotFoundException("Teslim edilecek kaynak staging dosyası bulunamadı.", sourceStagingFile);
        }

        var destDir = Path.GetDirectoryName(Path.GetFullPath(finalDestinationPath))!;
        Directory.CreateDirectory(destDir);

        // Aynı disk biriminde geçici teslimat dosyası (Atomik Rename/Move garantisi için)
        var tempDeliveryPath = Path.Combine(destDir, $"{Path.GetFileName(finalDestinationPath)}.{Guid.NewGuid():N}.delivery.tmp");

        try
        {
            // Staging dosyasını hedef diske kopyala
            await Task.Run(() => File.Copy(sourceStagingFile, tempDeliveryPath, overwrite: true), cancellationToken);

            // Atomik Move (.NET 10 POSIX/NT atomic rename semantics)
            File.Move(tempDeliveryPath, finalDestinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempDeliveryPath))
            {
                try
                {
                    File.Delete(tempDeliveryPath);
                }
                catch
                {
                    // Temizleme hatası bastırılır
                }
            }
        }
    }
}
