"""One-time model download for the pix2text worker (MFR 1.5, ONNX).
Uses the Windows certificate store (truststore) because the network performs TLS inspection.
Models land in <project root>\\tools\\models. No document content is sent anywhere."""
import os, sys, time
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MODEL_ROOT = os.path.join(ROOT, "tools", "models")
os.environ["YOLO_CONFIG_DIR"] = os.path.join(MODEL_ROOT, "ultralytics")
os.environ["HF_HOME"] = os.path.join(MODEL_ROOT, "hf")
os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
os.environ["HF_HUB_DISABLE_XET"] = "1"
import truststore
truststore.inject_into_ssl()
t0 = time.time()
from pix2text import LatexOCR
LatexOCR(model_name="mfr-1.5", model_backend="onnx", device="cpu", root=os.path.join(MODEL_ROOT, "pix2text"))
print(f"MODEL_READY seconds={time.time() - t0:.1f}")
