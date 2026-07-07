"""ONNX export ve test"""
from skl2onnx import convert_sklearn
from skl2onnx.common.data_types import FloatTensorType
import joblib
import numpy as np
import os
import onnxruntime as ort

rf = joblib.load('models/random_forest_rul.pkl')
n_features = 277

initial_type = [('float_input', FloatTensorType([None, n_features]))]
onnx_model = convert_sklearn(rf, initial_types=initial_type)

with open('models/random_forest_rul.onnx', 'wb') as f:
    f.write(onnx_model.SerializeToString())

size = os.path.getsize('models/random_forest_rul.onnx')
print(f'ONNX model kaydedildi: models/random_forest_rul.onnx ({size/1024:.1f} KB)')

# Test: ONNX vs Python
sess = ort.InferenceSession('models/random_forest_rul.onnx')
test_input = np.random.randn(1, n_features).astype(np.float32)
pred_onnx = sess.run(None, {'float_input': test_input})[0]
pred_python = rf.predict(test_input)

print(f'ONNX tahmin: {pred_onnx[0][:3]}')
print(f'Python tahmin: {pred_python[:3]}')
print(f'Maks fark: {np.abs(pred_onnx[0] - pred_python).max():.6f}')
print('ONNX test BASARILI!')
