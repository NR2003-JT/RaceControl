using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;

namespace RaceControl
{
    // Pictures of the choose cone board and the player's chooser: BoardRender <art folder> <out dir> [series]
    static class BoardRender
    {
        static ConeEntry E(int idx, int num, int lane, int row, int from)
        {
            int to = lane == 1 ? 2 * row - 1 : 2 * row;
            return new ConeEntry { Idx = idx, Num = num, Lane = lane, Row = row, From = from, To = to, Gained = from == 1 ? 0 : from - to };
        }

        [STAThread]
        static int Main(string[] args)
        {
            var art = new ConeArt { ExtraFolder = args[0], UseCompanion = false, SeriesChoice = args.Length > 2 ? args[2] : null };
            int[] nums = { 9, 88, 19, 24, 11, 22, 4, 14, 2, 8, 12, 20, 5, 1, 48, 17, 45, 23, 6, 3, 99, 21, 54, 47, 77, 7, 10 };
            var data = new ConeBoardData { Title = "CHOOSE CONE", Cars = nums.Length, Done = true };
            // single file order = nums; cars choose in that order, inside preferred, up front the best spot
            var rnd = new Random(3);
            for (int k = 0; k < nums.Length; k++)
            {
                bool ins = k == 1 || (k > 1 && (data.Inside.Count < data.Outside.Count + 2) && rnd.Next(3) > 0);
                if (k == 0) ins = false;
                var lane = ins ? data.Inside : data.Outside;
                lane.Add(E(100 + k, nums[k], ins ? 1 : 2, lane.Count + 1, k + 1));
            }
            Rectangle game = new Rectangle(0, 0, 1920, 1080);
            var board = new ConeBoardForm(data, art, game, new BoardLayout());
            foreach (var shot in new[] { Tuple.Create("board_page1", 0f), Tuple.Create("board_sliding", 2.2f), Tuple.Create("board_page2", 5f) })
            {
                board.PreviewFirst = shot.Item2;
                Save(board.Width, board.Height, board.Render, args[1] + "\\" + shot.Item1 + ".png");
            }
            var placing = new ConeBoardForm(data, art, game, new BoardLayout { Scale = 0.8 }, true);
            Save(placing.Width, placing.Height, placing.Render, System.IO.Path.Combine(args[1], "placing.png"));
            var me = 7;
            var offer = new ConeOffer
            {
                InsideCars = data.Inside.Where(e => e.Num != me && e.Row <= 9).ToList(),
                OutsideCars = data.Outside.Where(e => e.Num != me && e.Row <= 6).ToList(),
                PlayerNum = me, PlayerFrom = 20, Recommended = 1, PreferredLane = 1, Title = "CHOOSE CONE"
            };
            offer.Inside = offer.InsideCars.Select(e => e.Num).ToList();
            offer.Outside = offer.OutsideCars.Select(e => e.Num).ToList();
            var ch = new ConeChooser(offer, art, game, 1.0, l => { }, () => { });
            Save(ch.Width, ch.Height, ch.Render, System.IO.Path.Combine(args[1], "chooser.png"));
            ch.PreviewChoose(1);
            Save(ch.Width, ch.Height, ch.Render, System.IO.Path.Combine(args[1], "chooser_chosen.png"));
            Console.WriteLine("series " + art.Series + " (" + art.SeriesName + "), board " + board.Size + ", chooser " + ch.Size);
            return 0;
        }

        static void Save(int w, int h, Action<Graphics> render, string path)
        {
            using (var bmp = new Bitmap(w, h))
            {
                using (Graphics g = Graphics.FromImage(bmp)) render(g);
                bmp.Save(path, ImageFormat.Png);
            }
        }
    }
}
