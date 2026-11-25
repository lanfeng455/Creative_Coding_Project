using System;
using UnityEngine;

public class CloudMove : MonoBehaviour
{
    [SerializeField] Vector3 originPos =  Vector2.zero;
    [SerializeField] Vector3 targetPos =  Vector2.zero;
    [SerializeField] float speed = 1.0f;

    [SerializeField] private bool moveBackAndForth = false;
    [SerializeField] private bool moveLoop = true;

    private bool isMovingForth = true;

    private void Update()
    {
        if (moveBackAndForth)
        {
            if (isMovingForth)
            {
                transform.position = Vector2.MoveTowards(transform.position, targetPos, speed * Time.deltaTime);
                if (Mathf.Abs(transform.position.x - targetPos.x) < 0.1f)
                {
                    isMovingForth = false;
                }
            }
            else
            {
                transform.position = Vector2.MoveTowards(transform.position, originPos, speed * Time.deltaTime);
                if (Mathf.Abs(transform.position.x - originPos.x) < 0.1f)
                {
                    isMovingForth = true;
                }
            }
        }
        else
        {
            if (!isMovingForth && !moveLoop) return;
            transform.position = Vector3.MoveTowards(transform.position, targetPos, speed * Time.deltaTime);
            if (Mathf.Abs(transform.position.x - targetPos.x) < 0.1f && moveLoop)
            {
                transform.position = originPos;
            }
        }
    }
}
