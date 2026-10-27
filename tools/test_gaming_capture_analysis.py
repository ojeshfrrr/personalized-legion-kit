import unittest
from analyze_gaming_captures import metrics


class CaptureAnalysisTests(unittest.TestCase):
    def test_constant_frame_times(self):
        result = metrics([10.0] * 1000)
        self.assertEqual(result["averageFps"], 100)
        self.assertEqual(result["onePercentLowFps"], 100)
        self.assertEqual(result["p99FrameMs"], 10)

    def test_slow_frames_are_not_discarded(self):
        result = metrics([10.0] * 990 + [100.0] * 10)
        self.assertEqual(result["onePercentLowFps"], 10)
        self.assertLess(result["averageFps"], 100)

    def test_invalid_samples_are_rejected(self):
        for invalid in (0, -1, float("nan"), float("inf")):
            with self.assertRaises(ValueError):
                metrics([10.0] * 999 + [invalid])


if __name__ == "__main__":
    unittest.main()
