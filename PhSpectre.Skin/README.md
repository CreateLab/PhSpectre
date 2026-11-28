# Skin model

`Models/selfie_multiclass.fp16.onnx` is MediaPipe **Selfie Multiclass 256x256** (Apache-2.0), used to find face and
body skin for the Skin smoothing slider. It runs on the device; no picture leaves it.

- Source: <https://storage.googleapis.com/mediapipe-models/image_segmenter/selfie_multiclass_256x256/float32/latest/selfie_multiclass_256x256.tflite>
- Converted with `tf2onnx --opset 17`, then the weights were stored as float16 (a `Cast` in front of each brings
  them back to float32 when the model is loaded). Against the original it picks the same class on 99.97% of the
  pixels of a set of test portraits.
- Input: `1 x 256 x 256 x 3`, RGB in 0..1. Output: `1 x 256 x 256 x 6` logits for background, hair, body skin, face
  skin, clothes, others.
