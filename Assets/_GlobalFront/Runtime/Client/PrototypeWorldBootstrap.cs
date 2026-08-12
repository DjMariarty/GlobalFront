using System;
using GlobalFront.Core.Model;
using UnityEngine;
using CoreEntityId = GlobalFront.Core.Model.EntityId;

namespace GlobalFront.Client
{
    /// <summary>
    /// Creates a disposable primitive scene so camera and tick behavior can be
    /// tested before licensed art is selected. No generated object is saved.
    /// Units are created without authoritative EntityIds; the controller
    /// assigns server-generated EntityIds after match initialization.
    /// </summary>
    public sealed class PrototypeWorldBootstrap : MonoBehaviour
    {
        private const string PrototypeRootName = "[GlobalFront Prototype World]";

        private void Awake()
        {
#if !UNITY_SERVER
            if (GameObject.Find(PrototypeRootName) != null)
            {
                return;
            }

            var prototypeRoot = new GameObject(PrototypeRootName);
            var groundMaterial = CreateMaterial(new Color(0.19f, 0.24f, 0.18f));
            var blueMaterial = CreateMaterial(new Color(0.08f, 0.38f, 0.84f));
            var redMaterial = CreateMaterial(new Color(0.82f, 0.16f, 0.10f));
            var obstacleMaterial = CreateMaterial(new Color(0.30f, 0.31f, 0.33f));

            CreateGround(prototypeRoot.transform, groundMaterial);
            CreateArmy(
                prototypeRoot.transform,
                "AirCommand",
                new Vector3(-23f, 0f, -18f),
                blueMaterial,
                new PlayerId(1));
            CreateArmy(
                prototypeRoot.transform,
                "ChemicalSyndicate",
                new Vector3(23f, 0f, 18f),
                redMaterial,
                new PlayerId(2));
            CreateObstacles(prototypeRoot.transform, obstacleMaterial);
#endif
        }

        private static void CreateGround(Transform parent, Material material)
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Prototype Ground 120x120";
            ground.transform.SetParent(parent, false);
            ground.transform.localScale = new Vector3(12f, 1f, 12f);
            ground.GetComponent<Renderer>().sharedMaterial = material;
        }

        private static void CreateArmy(
            Transform parent,
            string armyName,
            Vector3 origin,
            Material material,
            PlayerId owner)
        {
            var armyRoot = new GameObject(armyName).transform;
            armyRoot.SetParent(parent, false);

            for (var row = 0; row < 4; row++)
            {
                for (var column = 0; column < 5; column++)
                {
                    var unit = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                    unit.name = $"{armyName} Unit {row * 5 + column + 1:00}";
                    unit.transform.SetParent(armyRoot, false);
                    unit.transform.position = origin + new Vector3(column * 3.2f, 1.1f, row * 3.2f);
                    unit.transform.localScale = new Vector3(1.1f, 1.1f, 1.1f);
                    unit.GetComponent<Renderer>().sharedMaterial = material;
                    unit.AddComponent<PrototypeUnit>().Initialize(owner);
                }
            }
        }

        private static void CreateObstacles(Transform parent, Material material)
        {
            for (var index = -2; index <= 2; index++)
            {
                var obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
                obstacle.name = $"Navigation Obstacle {index + 3}";
                obstacle.transform.SetParent(parent, false);
                obstacle.transform.position = new Vector3(index * 7f, 2f, index % 2 == 0 ? 3f : -3f);
                obstacle.transform.localScale = new Vector3(4f, 4f, 7f);
                obstacle.GetComponent<Renderer>().sharedMaterial = material;
            }
        }

        private static Material CreateMaterial(Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }

            if (shader == null)
            {
                throw new InvalidOperationException(
                    "GlobalFront prototype requires a compatible Lit shader.");
            }

            var material = new Material(shader)
            {
                color = color
            };
            return material;
        }
    }
}