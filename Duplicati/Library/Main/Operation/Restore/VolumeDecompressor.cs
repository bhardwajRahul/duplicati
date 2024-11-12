using System;
using System.Linq;
using System.Threading.Tasks;
using CoCoL;
using Duplicati.Library.Main.Database;
using Duplicati.Library.Main.Volumes;

namespace Duplicati.Library.Main.Operation.Restore
{
    internal class VolumeDecompressor
    {
        public static Task Run(LocalRestoreDatabase db, Options options)
        {
            return AutomationExtensions.RunTask(
            new
            {
                Input = Channels.decryptedVolume.ForRead,
                Output = Channels.decompressedVolumes.ForWrite
            },
            async self =>
            {
                try {
                    while (true)
                    {
                        var (block_id, volume_id, volume) = await self.Input.ReadAsync();

                        var bids = db.Connection.CreateCommand().ExecuteReaderEnumerable(@$"SELECT ID, Hash, Size FROM Block WHERE VolumeID = ""{volume_id}""").Select(x => (x.GetInt64(0), x.GetString(1), x.GetInt64(2))).ToArray();
                        var bid_lut = bids.ToDictionary(x => x.Item2);

                        using (var blocks = new BlockVolumeReader(options.CompressionModule, volume, options))
                        {
                            var volume_blocks = blocks.Blocks.ToArray();
                            for (int i = 0; i < volume_blocks.Length; i++)
                            {
                                byte[] buffer = new byte[options.Blocksize];
                        await self.Output.WriteAsync((block_id, buffer[..(int)bsize]));
                    }
                }
                catch (RetiredException ex)
                {
                    // NOP
                }
            });
        }
    }
}