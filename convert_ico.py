import sys
from PIL import Image

try:
    img = Image.open("appicon.png")
    img.save("appicon.ico", format='ICO', sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)])
    print("Success")
except Exception as e:
    print(e)
