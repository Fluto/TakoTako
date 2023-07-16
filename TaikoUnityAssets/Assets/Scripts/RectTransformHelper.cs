using System;
using UnityEngine;

namespace TakoTako
{
    public enum Pivot
    {
        BottomLeft = 0,
        BottomCentre,
        BottomRight,
        MiddleLeft,
        MiddleCentre,
        MiddleRight,
        TopLeft,
        TopCentre,
        TopRight,
    }

    public static class RectTransformHelper
    {
        public static void SetPivot(this RectTransform rectTransform, Pivot pivot)
        {
            float x, y;
            switch (pivot)
            {
                case Pivot.BottomLeft:
                    x = 0f;
                    y = 0f;
                    break;
                case Pivot.BottomCentre:
                    x = 0.5f;
                    y = 0f;
                    break;
                case Pivot.BottomRight:
                    x = 1f;
                    y = 0f;
                    break;
                case Pivot.MiddleLeft:
                    x = 0f;
                    y = 0.5f;
                    break;
                case Pivot.MiddleCentre:
                    x = 0.5f;
                    y = 0.5f;
                    break;
                case Pivot.MiddleRight:
                    x = 1f;
                    y = 0.5f;
                    break;
                case Pivot.TopLeft:
                    x = 0f;
                    y = 1f;
                    break;
                case Pivot.TopCentre:
                    x = 0.5f;
                    y = 1f;
                    break;
                case Pivot.TopRight:
                    x = 1f;
                    y = 1f;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(pivot), pivot, null);
            }

            SetPivot(rectTransform, new Vector2(x, y));
        }

        /// <summary>
        /// Set pivot without changing the position of the element
        /// </summary>
        public static void SetPivot(this RectTransform rectTransform, Vector2 pivot)
        {
            Vector3 deltaPosition = rectTransform.pivot - pivot; // get change in pivot
            deltaPosition.Scale(rectTransform.rect.size); // apply sizing
            deltaPosition.Scale(rectTransform.localScale); // apply scaling
            deltaPosition = rectTransform.localRotation * deltaPosition; // apply rotation

            rectTransform.pivot = pivot; // change the pivot
            rectTransform.localPosition -= deltaPosition; // reverse the position change
        }

        public static Vector3 GetPointOnCanvas(RectTransform canvas, Vector3 worldPosition)
        {
            return WorldProjectedToCanvasPlane(canvas, Camera.main, worldPosition);
        }

        public static Vector3 WorldProjectedToCanvasPlane(RectTransform canvas, Camera camera, Vector3 objectPosition)
        {
            Plane plane = new Plane(canvas.forward, canvas.position);
            Vector3 direction = camera.transform.position - objectPosition;
            var ray = new Ray(objectPosition, direction);
            if (!plane.Raycast(ray, out float distance))
                return objectPosition;
            return ray.GetPoint(distance);
        }
    }

}
